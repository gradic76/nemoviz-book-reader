use strict; use warnings;
use Encode qw(decode encode);

# sr.lang (Latin) -> sr-Cyrl.lang, by transliteration.
#
#   perl tools\sr-cyrillic.pl "Nemoviz Book Reader\Lang\sr.lang" "Nemoviz Book Reader\Lang\sr-Cyrl.lang"
#
# THIS IS A CONVERSION, NOT A TRANSLATION, and that is the whole reason it is a
# script. Serbian Latin and Cyrillic are a true bijection, so the Cyrillic file
# is derivable from the Latin one and must never be edited on its own -- correct
# sr.lang and run this again, or the two drift and only one of them is right.
#
# WHAT MUST NOT BE CONVERTED, and this is where a naive transliteration ruins
# the file. sr.lang is full of Latin that a reader has to TYPE or CLICK:
#
#   console.cloud.google.com      an address, in Cyrillic it is not an address
#   "Create new API key"          a button in somebody else's window
#   *.json  {0}  MP3  OCR  NBR    masks, placeholders, initialisms
#
# Serbian orthography allows Latin inside Cyrillic text for exactly this, and
# the alternative is a guide nobody can follow. So the protected runs are lifted
# out, the rest is converted, and they are put back.
#
# THE ESCAPES ARE A WORD TO A NAIVE SCAN. Measured on the first run: every
# \n in the file came out as \(Cyrillic en), which breaks every paragraph
# break in the language file. They are protected first, before anything else.
#
# THE DIGRAPHS ARE THE OTHER TRAP. lj nj dz+caron are ONE Cyrillic letter each
# -- but only when they are one sound. Across a morpheme seam they are two:
# nadziveti is not nadz+iveti, injekcija is not i+nj+ekcija. The exceptions are
# listed below rather than guessed at, and the script reports any word it
# converted that contains a digraph, so a new one can be caught by eye.

my %map = (
    'A'=>"\x{410}", 'B'=>"\x{411}", 'V'=>"\x{412}", 'G'=>"\x{413}", 'D'=>"\x{414}",
    "\x{110}"=>"\x{402}", 'E'=>"\x{415}", "\x{17D}"=>"\x{416}", 'Z'=>"\x{417}",
    'I'=>"\x{418}", 'J'=>"\x{408}", 'K'=>"\x{41A}", 'L'=>"\x{41B}", 'M'=>"\x{41C}",
    'N'=>"\x{41D}", 'O'=>"\x{41E}", 'P'=>"\x{41F}", 'R'=>"\x{420}", 'S'=>"\x{421}",
    'T'=>"\x{422}", "\x{106}"=>"\x{40B}", 'U'=>"\x{423}", 'F'=>"\x{424}",
    'H'=>"\x{425}", 'C'=>"\x{426}", "\x{10C}"=>"\x{427}", "\x{160}"=>"\x{428}",
    'a'=>"\x{430}", 'b'=>"\x{431}", 'v'=>"\x{432}", 'g'=>"\x{433}", 'd'=>"\x{434}",
    "\x{111}"=>"\x{452}", 'e'=>"\x{435}", "\x{17E}"=>"\x{436}", 'z'=>"\x{437}",
    'i'=>"\x{438}", 'j'=>"\x{458}", 'k'=>"\x{43A}", 'l'=>"\x{43B}", 'm'=>"\x{43C}",
    'n'=>"\x{43D}", 'o'=>"\x{43E}", 'p'=>"\x{43F}", 'r'=>"\x{440}", 's'=>"\x{441}",
    't'=>"\x{442}", "\x{107}"=>"\x{45B}", 'u'=>"\x{443}", 'f'=>"\x{444}",
    'h'=>"\x{445}", 'c'=>"\x{446}", "\x{10D}"=>"\x{447}", "\x{161}"=>"\x{448}",
);
my $LJ = "\x{409}"; my $lj = "\x{459}";
my $NJ = "\x{40A}"; my $nj = "\x{45A}";
my $DZ = "\x{40F}"; my $dz = "\x{45F}";

# Words where lj / nj / dz+caron span a morpheme seam and are TWO letters.
my @seams = qw(nadziveti nadzivi nadzive injekcij konjug konjunk nadjaca
               nadjacati tanjug vanjezicki izvanjez);

# Latin that must survive as Latin.
my @protect = (
    qr/\\[rn]/,                                    # the \n escape -- its own n is NOT a word
    qr/\{[0-9]+\}/,                                  # {0} {1}
    qr/\*\.[A-Za-z0-9]+/,                            # *.json
    qr/[A-Za-z][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+){1,}(?:\/[^\s"]*)?/,   # domains, files
    qr/\b[A-Z][A-Z0-9]{1,}\b/,                       # NBR OCR API JSON MP3 CD LGPL
    qr/\bGPL\b|\bMIT\b/,
    qr/"[^"]*"/,                                     # anything the reader must click
    qr/\x{201E}[^\x{201C}\x{201D}]*[\x{201C}\x{201D}]/,
);
my @names = qw(Nemoviz Book Reader Claude Anthropic Google Cloud Azure Speech
               Microsoft Windows Gemini DeepSeek OpenAI GPT Terra Luna Sol
               Text-to-Speech Compact Disc Digital Audio Andika Atkinson
               Hyperlegible Next Lexend OpenDyslexic Luciole SIL Open Font
               License Creative Commons Attribution Apache libmpv FFmpeg
               liblouis NVDA Controller Client TagLib PdfPig SharpCompress
               Nemogu\x{107}a vizija Gordan Radi\x{107} Enter Control Shift Ctrl
               Alt Space Sign up Top Billing Create new key Done Personal
               Organization Add balance Start free Select Name Keys Permissions
               Credentials Application restrictions None Web Xing);

# A FEW VALUES ARE NOT A TRANSLITERATION OF THE LATIN ONE, and the language's
# own name is the first of them. Two rows reading "Srpski" and "Srpski" in
# Cyrillic are the SAME WORD to a screen reader -- a blind reader picking a
# language would hear it twice and have no way to tell them apart. Each names
# its own script instead.
my %override = (
    'LanguageName'      => "\x{421}\x{440}\x{43F}\x{441}\x{43A}\x{438} (\x{45B}\x{438}\x{440}\x{438}\x{43B}\x{438}\x{446}\x{430})",
    # CYRILLIC IS WIDER THAN LATIN, and the drawn legends have a hard column.
    # Measured at 12 pt Segoe: the Latin "Biblioteka" is 76 units, its exact
    # transliteration is 93, and the column is 91 -- so a faithful conversion
    # would have been clipped on the key. "Knjige" is 52 and says what is behind
    # the key; the spoken name stays "Biblioteka, F3", which is the same split
    # English already makes between "Bookmark" on the key and "Set Bookmark, F5"
    # in the ear. Only the LEGEND is overridden.
    'Btn.Library.Legend' => "\x{41A}\x{45A}\x{438}\x{433}\x{435}",
);

my ($in, $out) = @ARGV;
die "usage: sr-cyrillic.pl <sr.lang> <sr-Cyrl.lang>\n" unless $in && $out;

open(my $h, '<:raw', $in) or die "$in: $!\n";
my $text = decode('UTF-8', do { local $/; <$h> }); close $h;

my (%seen, $words, $digraphWords) = ((), 0, 0);

sub cyr {
    my $w = shift;
    my $low = lc $w;
    my $seam = 0;
    for my $s (@seams) { $seam = 1 if index($low, $s) >= 0 }
    my $r = '';
    my @c = split //, $w;
    for (my $i = 0; $i < @c; $i++) {
        my $two = $i + 1 < @c ? $c[$i] . $c[$i+1] : '';
        if (!$seam && $two ne '') {
            my $l2 = lc $two;
            if ($l2 eq 'lj') { $r .= ($c[$i] eq 'L' ? ($c[$i+1] eq 'J' ? $LJ : $LJ) : $lj); $i++; next; }
            if ($l2 eq 'nj') { $r .= ($c[$i] eq 'N' ? $NJ : $nj); $i++; next; }
            if ($l2 eq "d\x{17E}" || $l2 eq "d\x{17E}") { $r .= ($c[$i] eq 'D' ? $DZ : $dz); $i++; next; }
        }
        $r .= exists $map{$c[$i]} ? $map{$c[$i]} : $c[$i];
    }
    $words++;
    $digraphWords++ if $w =~ /lj|nj|d\x{17E}/i;
    return $r;
}

my @lines = split /\n/, $text, -1;
for my $line (@lines) {
    next if $line =~ /^\s*$/;
    # comments and keys are untouched; only the value is converted
    if ($line =~ /^([A-Za-z][A-Za-z0-9._]*=)(.*)$/s) {
        my ($k, $v) = ($1, $2);
        my @keep;
        for my $re (@protect) {
            $v =~ s/($re)/push @keep, $1; "\x{FFFC}" . (scalar(@keep) - 1) . "\x{FFFD}"/ge;
        }
        for my $nm (@names) {
            $v =~ s/\b\Q$nm\E\b/push @keep, $nm; "\x{FFFC}" . (scalar(@keep) - 1) . "\x{FFFD}"/ge;
        }
        $v =~ s/([A-Za-z\x{10C}\x{10D}\x{106}\x{107}\x{160}\x{161}\x{17D}\x{17E}\x{110}\x{111}]+)/cyr($1)/ge;
        # PUT THEM BACK IN A LOOP, because the protections NEST. A {0} inside
        # quotes is lifted twice -- once as a placeholder, then again inside the
        # quoted run that now holds its marker -- so one pass restores the outer
        # and leaves the inner marker sitting in the text as an invisible
        # character. Measured: four keys lost their {0} that way.
        1 while $v =~ s/\x{FFFC}([0-9]+)\x{FFFD}/$keep[$1]/g;
        my $bare = $k; $bare =~ s/=$//;
        $v = $override{$bare} if exists $override{$bare};
        $line = $k . $v;
    }
}

my $res = join "\n", @lines;
$res =~ s/^; Translated by:(\s+)Claude \(Anthropic\), 2026/; Translated by:$1Claude (Anthropic), 2026 - Latin, then transliterated by tools\/sr-cyrillic.pl/m;
open(my $o, '>:raw', $out) or die "$out: $!\n";
print $o encode('UTF-8', $res); close $o;
printf "%s: %d words converted, %d of them held a digraph\n", $out, $words, $digraphWords;
