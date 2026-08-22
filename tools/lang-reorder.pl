use strict; use warnings;

# Rewrite a .lang file in the order tools/lang-order.txt lays down.
#
#   perl tools\lang-reorder.pl "Nemoviz Book Reader\Lang\en.lang" [more.lang ...]
#
# IT SYNCS AS WELL AS ORDERS, and en.lang is the source. For every other
# language: a key the source has and this file has not is copied in with its
# ENGLISH text and put at the foot under NOT YET TRANSLATED, carrying a note
# naming the section it belongs to; a key the source no longer has is dropped.
#
# WHAT IS PENDING IS DECIDED BY POSITION, NEVER BY THE TEXT. Measured
# 2026-08-22: 21 Croatian keys are identical to their English -- Format,
# Status, Track, Audio CD, Font, Common.Dash. A tool that read "same as
# English" as "untranslated" would park those at the foot for ever and the
# translator would keep re-translating them. So everything under the marker is
# filed on the next run without asking, and the tool only REPORTS which came
# back unchanged -- a forgotten key is visible without a legitimate one being
# misfiled.
#
# WHAT IT DOES TO THE COMMENTS, and this is the point of it as much as the
# order. Every comment in the file is DROPPED except the credit block and the
# section headers this writes itself. Gordan, 2026-08-22: "Ima dosta nekih
# programskih napomena viška, koje više tebi i meni objašnjavaju zašto je nešto
# postavljeno nego budućem prevoditelju." He is right, and one of them was worse
# than surplus -- a note claiming one Latin recognizer reads every Latin
# language, which he had already corrected in the code months earlier. A comment
# nobody re-reads goes stale silently, and a translator is not the person to
# explain our reasoning to. The reasoning lives in the code beside the thing it
# explains, and in git, both of which get re-read when that thing changes.
#
# EVERYTHING THIS WRITES IS IN ENGLISH, in every language file (Gordan's rule,
# 2026-08-22: the comments are either always English or always in the target
# language, never mixed). English, because the KEYS are English and en.lang is
# the source of truth -- somebody who cannot read "PLAYER - the info glass"
# cannot read Player.Info.TitleLabel either. The other choice would mean
# translating these 29 headings for every new language and maintaining them
# apart, and the first language that skipped it would put the mixture back.
#
# THE CREDIT BLOCK IS CARRIED FORWARD, NEVER REGENERATED OVER. A human who
# writes their name into "Verified and modified by" must find it there after the
# next reorder, or the tool quietly erases the one line in the file that is not
# ours to write.
#
# RUN IT ON EVERY LANGUAGE AT ONCE. The files are only comparable while they
# share an order; reordering one is how they stop being.

my $BS = chr(92);                       # no backslash may appear in this source

my $orderFile = 'tools/lang-order.txt';
$orderFile = 'lang-order.txt' unless -f $orderFile;
open(my $of, '<:raw', $orderFile) or die "$orderFile: $!\n";
my (@plan, %never);
while (my $l = <$of>) {
    $l =~ s/\r?\n$//;
    next if $l =~ /^\s*$/ || $l =~ /^#/;
    if ($l =~ /^=\s*(.+)$/) { push @plan, ['SECTION', $1]; next; }
    # A line starting with ! is a key that belongs to the SOURCE ONLY and is
    # never offered for translation. App.Name is the case that found this: the
    # product's name is not translated (CLAUDE.md §3), and without this the sync
    # dutifully put it in front of every translator as work to do.
    if ($l =~ /^!\s*(.+)$/) { $never{$1} = 1; push @plan, ['PREFIX', $1]; next; }
    push @plan, ['PREFIX', $l];
}
close $of;
die "no plan\n" unless @plan;

# The source of truth, read once. Everything else is measured against it.
my (%src, @srcOrder);
{
    my $en = $ARGV[0];
    for my $c (@ARGV) { $en = $c if $c =~ /\ben\.lang$/i }
    open(my $e, '<:raw', $en) or die "$en: $!\n";
    while (my $l = <$e>) {
        $l =~ s/\r?\n$//;
        next if $l =~ /^\s*$/ || $l =~ /^[;#]/;
        my $i = index($l, '=');
        next if $i <= 0;
        my $k = substr($l, 0, $i);
        next if exists $src{$k};
        $src{$k} = substr($l, $i + 1);
        push @srcOrder, $k;
    }
    close $e;
}

for my $file (@ARGV) {
    my $isSource = ($file =~ /\ben\.lang$/i);

    open(my $h, '<:raw', $file) or die "$file: $!\n";
    my @lines = <$h>; close $h;

    # Keep whatever the credit lines already say -- a translator's own name
    # lives here and this tool must not be able to lose it.
    my @credit;
    for my $l (@lines) {
        (my $b = $l) =~ s/\r?\n$//;
        push @credit, $b if $b =~ /^;\s*(Translated by|Written by|Verified and modified by)\s*:/i;
    }

    # Which keys stood BELOW the pending marker when this file was read. They
    # are what the translator was given to do, and they are filed on this run
    # whatever they now say -- see the note at the head about why position and
    # not text decides that.
    my (@keys, %val, $nl, %wasPending);
    my $below = 0;
    for my $l (@lines) {
        $nl ||= ($l =~ /(\r?\n)$/) ? $1 : "\n";
        (my $b = $l) =~ s/\r?\n$//;
        $below = 1 if $b =~ /^;\s*-+\s*NOT YET TRANSLATED/i;
        next if $b =~ /^\s*$/ || $b =~ /^[;#]/;
        my $i = index($b, '=');
        next if $i <= 0;
        my $k = substr($b, 0, $i);
        next if exists $val{$k};          # first wins; en.lang had a few twins
        $val{$k} = substr($b, $i + 1);
        $wasPending{$k} = 1 if $below;
        push @keys, $k;
    }
    $nl ||= "\n";

    # RECONCILE WITH THE SOURCE. Not for en.lang, which IS the source.
    my (@pending, @dropped, @unchanged);
    unless ($isSource) {
        my %have = map { $_ => 1 } @keys;
        for my $k (@srcOrder) {
            next if $have{$k} || $never{$k};
            $val{$k} = $src{$k};              # the English text, so there is
            push @keys, $k;                   # something to translate FROM
            push @pending, $k;
        }
        # A key the source has dropped is dead weight in every language.
        my @live;
        for my $k (@keys) {
            if (exists $src{$k}) { push @live, $k }
            else { push @dropped, $k; delete $val{$k} }
        }
        @keys = @live;
        # Anything that came back reading exactly as the English does. Reported,
        # never acted on: 21 Croatian keys are legitimately identical.
        for my $k (@keys) {
            push @unchanged, $k if $wasPending{$k} && $val{$k} eq $src{$k};
        }
    }

    unless (@credit) {
        @credit = $isSource
            ? ('; Written by:               Gordan Radi' . chr(263))
            : ('; Translated by:            Claude (Anthropic), 2026',
               '; Verified and modified by: ');
    }

    my @out;
    push @out, "; Nemoviz Book Reader" . $nl;
    push @out, ";" . $nl;
    push @out, $_ . $nl for @credit;
    unless ($isSource) {
        push @out, ";" . $nl;
        push @out, "; Human translators: write your name and the year after the colon above." . $nl;
        push @out, "; If somebody has signed there already, add another line like theirs" . $nl;
        push @out, "; underneath - every one of them is kept when this file is reordered." . $nl;
    }
    push @out, ";" . $nl;
    push @out, "; Translate what stands to the RIGHT of the equals sign, and nothing else." . $nl;
    push @out, "; Anything in braces - {0}, {1} - is filled in by the program and has to" . $nl;
    push @out, "; survive. The two characters " . $BS . "n mean a line break and " . $BS . "n" . $BS . "n a blank line;" . $nl;
    push @out, "; never break a line with a real Enter, because everything after it is" . $nl;
    push @out, "; thrown away when the file is read." . $nl;
    push @out, ";" . $nl;
    push @out, "; The order is the same in every language and is set in" . $nl;
    push @out, "; tools/lang-order.txt. It follows the program as it is used: the player" . $nl;
    push @out, "; and its panel, then window by window, and inside a window the thing" . $nl;
    push @out, "; itself before the dialogs it opens." . $nl;

    my %pend = map { $_ => 1 } @pending;
    my (%placed, %section, $here);
    for my $step (@plan) {
        my ($kind, $what) = @$step;
        if ($kind eq 'SECTION') { $here = $what; push @out, $nl, "; ---- $what" . $nl; next; }
        for my $k (@keys) {
            next if $placed{$k};
            next unless index($k, $what) == 0;
            $placed{$k} = 1;
            $section{$k} = $here;
            # A key waiting to be translated is NOT written into its section.
            # It goes to the foot, where one Ctrl+End finds the whole job.
            push @out, $k . '=' . $val{$k} . $nl unless $pend{$k};
        }
    }

    if (@pending) {
        push @out, $nl, "; ---- NOT YET TRANSLATED" . $nl;
        push @out, "; " . $nl;
        push @out, "; These are new since this file was last translated, and they" . $nl;
        push @out, "; still read as the English does. Translate them here; the next" . $nl;
        push @out, "; run of the tool files each one into the section named above it." . $nl;
        for my $k (@pending) {
            next unless $placed{$k};
            push @out, $nl, "; belongs in: " . ($section{$k} || '?') . $nl;
            push @out, $k . '=' . $val{$k} . $nl;
        }
    }

    my @orphans = grep { !$placed{$_} } @keys;
    if (@orphans) {
        push @out, $nl, "; ---- everything else (add these to tools/lang-order.txt)" . $nl;
        push @out, $_ . '=' . $val{$_} . $nl for @orphans;
    }

    open(my $o, '>:raw', $file) or die $!; print $o @out; close $o;
    printf "%s: %d keys", $file, scalar(@keys);
    printf ", %d PENDING translation", scalar(@pending) if @pending;
    printf ", %d dropped (gone from the source)", scalar(@dropped) if @dropped;
    printf ", %d orphans (%s)", scalar(@orphans), join(', ', @orphans) if @orphans;
    print "\n";
    print "    dropped: ", join(', ', @dropped), "\n" if @dropped;
    if (@unchanged) {
        print "    came back reading exactly as the English does -- confirm that is meant:\n";
        print "      $_\n" for @unchanged;
    }
}
