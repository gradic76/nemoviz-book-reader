use strict; use warnings;

# Rewrite a .lang file in the order tools/lang-order.txt lays down.
#
#   perl tools\lang-reorder.pl "Nemoviz Book Reader\Lang\en.lang" [more.lang ...]
#
# WHAT IT DOES TO THE COMMENTS, and this is the point of it as much as the
# order. Every comment in the file is DROPPED except the section headers this
# writes itself. Gordan, 2026-08-22: "Ima dosta nekih programskih napomena
# viška, koje više tebi i meni objašnjavaju zašto je nešto postavljeno nego
# budućem prevoditelju." He is right, and one of them was worse than surplus --
# a note claiming one Latin recognizer reads every Latin language, which he had
# already corrected in the code months earlier. A comment nobody re-reads is a
# comment that goes stale silently, and a translator is not the person to
# explain our reasoning to.
#
# The reasoning is not lost: it lives in the code beside the thing it explains,
# and in git, both of which get re-read when the thing changes.
#
# RUN IT ON EVERY LANGUAGE AT ONCE. The files are only comparable while they
# share an order; reordering one is how they stop being.

my $orderFile = 'tools/lang-order.txt';
$orderFile = 'lang-order.txt' unless -f $orderFile;
open(my $of, '<:raw', $orderFile) or die "$orderFile: $!\n";
my (@plan, $section);
while (my $l = <$of>) {
    $l =~ s/\r?\n$//;
    next if $l =~ /^\s*$/ || $l =~ /^#/;
    if ($l =~ /^=\s*(.+)$/) { push @plan, ['SECTION', $1]; next; }
    push @plan, ['PREFIX', $l];
}
close $of;
die "no plan\n" unless @plan;

for my $file (@ARGV) {
    open(my $h, '<:raw', $file) or die "$file: $!\n";
    my @lines = <$h>; close $h;

    my (@keys, %val, $nl);
    for my $l (@lines) {
        $nl ||= ($l =~ /(\r?\n)$/) ? $1 : "\n";
        (my $b = $l) =~ s/\r?\n$//;
        next if $b =~ /^\s*$/ || $b =~ /^[;#]/;
        my $i = index($b, '=');
        next if $i <= 0;
        my $k = substr($b, 0, $i);
        next if exists $val{$k};          # first wins; en.lang has a few twins
        $val{$k} = substr($b, $i + 1);
        push @keys, $k;
    }
    $nl ||= "\n";

    my (@out, %placed);
    push @out, "; Nemoviz Book Reader" . $nl;
    push @out, "; " . $nl;
    push @out, "; Redoslijed je isti u svim jezicima i propisan je u" . $nl;
    push @out, "; tools/lang-order.txt. Ide onako kako se program koristi:" . $nl;
    push @out, "; player i njegov panel, pa prozor po prozor, a unutar prozora" . $nl;
    push @out, "; ono glavno prije dijaloga koje otvara." . $nl;
    push @out, "; " . $nl;
    push @out, "; Prevodi se ono desno od znaka jednakosti. Sve u vitičastim" . $nl;
    push @out, "; zagradama - {0}, {1} - program popunjava sam i mora ostati." . $nl;
    push @out, "; Dva znaka \\n znače prazan redak; nikad ne prelamajte redak" . $nl;
    push @out, "; stvarnim Enterom, jer sve iza njega program ne vidi." . $nl;

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
        push @out, $nl, "; ---- sve ostalo (dodajte ih u tools/lang-order.txt)" . $nl;
        push @out, $_ . '=' . $val{$_} . $nl for @orphans;
    }

    open(my $o, '>:raw', $file) or die $!; print $o @out; close $o;
    printf "%s: %d keys, %d placed, %d orphans%s\n",
           $file, scalar(@keys), scalar(keys %placed), scalar(@orphans),
           @orphans ? " (" . join(', ', @orphans) . ")" : "";
}
