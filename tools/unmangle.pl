#!/usr/bin/perl
#
#   perl tools/unmangle.pl <report-file> <file> [<file> ...]
#
# Repairs text that was written out as UTF-8 after having been read as though it
# were an ANSI code page — the double encoding that turned "▶" into "â–¶" and
# left the player's four transport buttons drawing garbage (commit 270fcdf).
# It rewrites the files in place and lists every change in the report, which is
# the thing to read before believing it.
#
# The page here is cp1250, not cp1252: this machine's ANSI page is the Central
# European one. That distinction is the whole reason commit 54c0132 only got
# half the damage — everything that exists ONLY in cp1250 stayed broken and
# invisible to a cp1252 search. cp1252 is still tried second.
#
# A run of 2-3 non-ASCII characters is rewritten only when it round-trips: it
# must encode to the ANSI page, those bytes must be valid UTF-8, that must give
# back exactly ONE character, and re-encoding that character must reproduce the
# same bytes. Anything less lets real text through the mill — see the two traps
# marked below, both of which cost real time and one of which quietly ate Czech
# and Slovak words out of the language detector's stopword lists.
#
# What it will NOT touch: a lone accented letter between ASCII (Romanian "â",
# Croatian "č"), and a run like "──" that no ANSI page can encode.
use strict;
use warnings;
use Encode qw(decode encode);

my $report = shift @ARGV;
open(my $rep, '>', $report) or die $!;

for my $file (@ARGV) {
    open(my $in, '<:raw', $file) or die "$file: $!";
    my $raw = do { local $/; <$in> };
    close $in;

    my $s = eval { decode('UTF-8', $raw, Encode::FB_CROAK) };
    unless (defined $s) { print $rep "SKIPPED (not UTF-8): $file\n"; next; }

    my $hits = 0;
    # cp1250 first: this machine's ANSI page is the Central European one, and it
    # is what actually did the damage. cp1252 stays as a second try because the
    # two agree on much of the range and older wounds may have come from it.
    my $fix = sub {
        my $t = shift;
        for my $page ('cp1250', 'cp1252') {
            # EVERY encode/decode here gets a copy: with a CHECK value, Encode
            # empties whatever it consumed out of its argument. Passing the
            # originals silently returned an empty string and deleted the very
            # characters this script exists to restore.
            my ($tc, $dc) = ($t, undef);
            my $b = eval { encode($page, $tc, Encode::FB_CROAK) };
            next unless defined $b && length $b;
            my $copy = $b;
            my $d = eval { decode('UTF-8', $copy, Encode::FB_CROAK) };
            next unless defined $d;
            # The decode alone is not proof: Perl will accept an over-long
            # 5-byte lead, which is how Czech "může" (ů + ž = F9 9E under
            # cp1250) came out as "me". Demand ONE character back, and demand
            # that re-encoding it gives exactly the bytes we started from.
            next unless length($d) == 1;
            $dc = $d;
            my $back = eval { encode('UTF-8', $dc, Encode::FB_CROAK) };
            # Compared as hex, because one side can carry Perl's UTF8 flag and
            # then "eq" compares characters against bytes and always fails.
            next unless defined $back && unpack("H*", $back) eq unpack("H*", $b);
            $hits++;
            print $rep "  $file: $page [$t] -> [$d]\n";
            return $d;
        }
        return $t;
    };

    # Longest first: a three-character run is a three-byte character.
    $s =~ s/([^\x00-\x7F]{3})/$fix->($1)/ge;
    $s =~ s/([^\x00-\x7F]{2})/$fix->($1)/ge;

    next unless $hits;
    open(my $out, '>:raw', $file) or die $!;
    print $out encode('UTF-8', $s);
    close $out;
    print $rep "$file: $hits repaired\n";
}
close $rep;
