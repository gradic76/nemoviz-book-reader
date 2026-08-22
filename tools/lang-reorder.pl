use strict; use warnings;

# Rewrite a .lang file in the order tools/lang-order.txt lays down.
#
#   perl tools\lang-reorder.pl "Nemoviz Book Reader\Lang\en.lang" [more.lang ...]
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
my @plan;
while (my $l = <$of>) {
    $l =~ s/\r?\n$//;
    next if $l =~ /^\s*$/ || $l =~ /^#/;
    if ($l =~ /^=\s*(.+)$/) { push @plan, ['SECTION', $1]; next; }
    push @plan, ['PREFIX', $l];
}
close $of;
die "no plan\n" unless @plan;

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

    my (@keys, %val, $nl);
    for my $l (@lines) {
        $nl ||= ($l =~ /(\r?\n)$/) ? $1 : "\n";
        (my $b = $l) =~ s/\r?\n$//;
        next if $b =~ /^\s*$/ || $b =~ /^[;#]/;
        my $i = index($b, '=');
        next if $i <= 0;
        my $k = substr($b, 0, $i);
        next if exists $val{$k};          # first wins; en.lang had a few twins
        $val{$k} = substr($b, $i + 1);
        push @keys, $k;
    }
    $nl ||= "\n";

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

    my %placed;
    for my $step (@plan) {
        my ($kind, $what) = @$step;
        if ($kind eq 'SECTION') { push @out, $nl, "; ---- $what" . $nl; next; }
        for my $k (@keys) {
            next if $placed{$k};
            next unless index($k, $what) == 0;
            $placed{$k} = 1;
            push @out, $k . '=' . $val{$k} . $nl;
        }
    }

    my @orphans = grep { !$placed{$_} } @keys;
    if (@orphans) {
        push @out, $nl, "; ---- everything else (add these to tools/lang-order.txt)" . $nl;
        push @out, $_ . '=' . $val{$_} . $nl for @orphans;
    }

    open(my $o, '>:raw', $file) or die $!; print $o @out; close $o;
    printf "%s: %d keys, %d placed, %d orphans, credit lines %d%s\n",
           $file, scalar(@keys), scalar(keys %placed), scalar(@orphans), scalar(@credit),
           @orphans ? " (" . join(', ', @orphans) . ")" : "";
}
