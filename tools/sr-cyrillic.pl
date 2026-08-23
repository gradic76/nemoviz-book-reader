use strict; use warnings;
use Encode qw(decode encode);

# The flag is read HERE, before @protect is built, because @protect itself
# is filtered by it -- see the note beside @protectLangOnly.
my $textMode = 0;
@ARGV = grep { $_ eq q{--text} ? ($textMode = 1, 0)[1] : 1 } @ARGV;

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
    # {0} {1} in a .lang file, and {beta} in the manual. LETTERS ARE ALLOWED
    # HERE, and the numeric-only version cost the Cyrillic manual its beta
    # notice: docs/help/sr.txt marks a heading "{beta}", the rule did not match
    # it, "beta" transliterated to Cyrillic, and tools/make-help.pl then found
    # no marker to act on -- so the one page that had to warn a reader that the
    # feature is switched off silently did not. Nothing in Serbian is written
    # inside braces, so widening this cannot swallow a real word.
    qr/\{[A-Za-z0-9]+\}/,
    qr/\*\.[A-Za-z0-9]+/,                            # *.json
    qr/[A-Za-z][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+){1,}(?:\/[^\s"]*)?/,   # domains, files

    # A BARE DOTTED EXTENSION, written the way prose writes it: .rtf, .epub,
    # .zip, and .NET while we are here. The mask rule wants a star in front and
    # the domain rule wants a name in front, so a list of formats -- which is
    # most of the manual's second chapter -- had nothing protecting it and came
    # out as ".ртф", ".епуб", ".рар". Those are not extensions; a reader cannot
    # type them and cannot recognise them on a file. Safe because it needs a
    # letter IMMEDIATELY after the dot, and a Serbian sentence puts a space
    # there.
    #
    # IT MUST STAND AFTER THE DOMAIN RULE, and putting it before cost one
    # regression to find out: it matched the ".com" of onmicrosoft.com first,
    # leaving "onmicrosoft" bare for the transliteration, so Azure's own
    # directory name came out as "онмицрософт.com". The general form has to get
    # its chance before the narrow one.
    qr/\.[A-Za-z][A-Za-z0-9]*\b/,
    qr/\b[A-Z][A-Z0-9]{1,}\b/,                       # NBR OCR API JSON MP3 CD LGPL
    qr/\bGPL\b|\bMIT\b/,

    # A SINGLE LETTER AFTER A MODIFIER IS A KEY ON THE KEYBOARD, not a word.
    # The uppercase rule above cannot reach it -- it needs two characters, and
    # a lone "B" is one. Found 2026-08-23 in sr-Cyrl.lang: Prop.Bypass.Shortcut
    # read "Control Б", and there is no such key on any keyboard. Worse than a
    # mistranslation: a reader who trusts it presses a key that does nothing,
    # and the only way to find the real one is to guess.
    #
    # The arrow keys are NOT affected and must not be: they are named by
    # direction, so "Ctrl+Лево" is right and "Ctrl+Left" would be wrong. Only a
    # lone Latin LETTER is a key name here.
    qr/\b(?:Ctrl|Control|Alt|Shift|Win)\s*\+?\s*[A-Za-z]\b/,

    # A TOKEN THAT MIXES LETTERS AND DIGITS IS AN IDENTIFIER, NOT A WORD.
    # Serbian has no word with a digit inside it, so nothing native can match
    # this -- but version strings, key names and model numbers are full of them,
    # and the uppercase rule above cannot help because it requires the token to
    # BEGIN with a letter. Found 2026-08-23 in the manual: Windows 10 "22H2"
    # came out as "22X2" with a Cyrillic X, and it is exactly the kind of damage
    # tools/check-scripts.pl cannot see -- once the H has become an X there is no
    # Latin left in the word to mix. A reader would have typed a version that
    # does not exist.
    qr/\b(?=[A-Za-z0-9]*[0-9])(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*\b/,

    # A WORD CONTAINING q, w, x OR y CANNOT BE TRANSLITERATED AT ALL, and this
    # rule is worth more than every name in the list below.
    #
    # The Latin-Cyrillic bijection this script rests on covers the Serbian
    # alphabet, and those four letters are not in it -- %map has no entry for
    # any of them. So cyr() converts every letter around them and leaves them
    # standing, and the result is a word in two alphabets: Directory came out
    # as "Дирецторy", Windowsa as "Wиндоwса", Copyleft as "Цопyлефт", RegEx as
    # "РегЕx", keys as "кеyс", Text as "Теxт". Six of them, found 2026-08-22 by
    # tools/check-scripts.pl and by nothing else, because each looks like a
    # plausible Serbian word until you read the letters one at a time.
    #
    # The @names list below could not have caught them and adding them to it
    # would be treating the symptom: "Windows" IS in that list and still lost,
    # because Serbian inflects it and \bWindows\b does not match "Windowsa".
    # This rule needs no list and no maintenance -- whatever the right answer
    # for such a word is (keep it Latin, or transcribe it by hand into the
    # %override table), a HALF-converted word is never it.
    # THE HYPHEN IS PART OF THE WORD HERE, and leaving it out broke a name this
    # script had always got right. @protect runs before @names, so a bare
    # [A-Za-z] version of this rule lifted "Text" out of "Text-to-Speech" on
    # account of its x -- and \bText-to-Speech\b could then no longer match what
    # was left, so the "to" in the middle transliterated and Google1s own
    # service became "Text-то-Speech". Allowing hyphens takes the whole name in
    # one piece. Serbian has no q, w, x or y of its own, so this cannot swallow
    # a Serbian word.
    qr/\b[A-Za-z-]*[QqWwXxYy][A-Za-z-]*\b/,

    qr/"[^"]*"/,                                     # anything the reader must click
    qr/\x{201E}[^\x{201C}\x{201D}]*[\x{201C}\x{201D}]/,
);

# THE QUOTE RULES ARE FOR .lang FILES ONLY, and prose is why. In a language
# file a quoted run is a button in somebody else's window -- "Create new API
# key" -- and must survive as Latin or the guide cannot be followed. In the
# manual, quotes are ordinary punctuation: the Croatian says the arrows
# "premotavate" five seconds at a time, using the quotes to mark a metaphor,
# and protecting that left a Serbian verb sitting in Latin in the middle of a
# Cyrillic sentence. The manual marks UI names with *stars* instead, which the
# generator turns into emphasis and which no rule here touches.
my @protectLangOnly = (
    qr/"[^"]*"/,
    qr/\x{201E}[^\x{201C}\x{201D}]*[\x{201C}\x{201D}]/,
);
@protect = grep { my $r = "$_"; !grep { "$_" eq $r } @protectLangOnly } @protect if $textMode;
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

# A SECOND LIST, AND IT HAS TO BE MATCHED AS PHRASES RATHER THAN WORDS.
#
# Everything above is one word, and a word list cannot protect a BUTTON whose
# name contains an ordinary little English word: "Add to balance" came out as
# "Add то balance" because Add and balance were both protected and the "to"
# between them was not -- and "to" is also the Serbian word "то", so it cannot
# simply be added to the list above without turning real Serbian prose into
# Latin. The phrase is the unit that carries the meaning here, so the phrase is
# what gets lifted out.
#
# Found 2026-08-22 by comparing sr.lang against sr-Cyrl.lang word by word and
# keeping only words that ALSO stand in en.lang -- i.e. English that the Serbian
# translator deliberately left in English, and that the transliteration then
# ate. That comparison is the check to re-run after touching this file; most of
# what it reports is coincidence (audio, format, problem, signal and disk are
# Serbian words too, and must be Cyrillic), so it is read rather than obeyed.
my @phrases = (
    'Add to balance', 'Create new secret key', 'Create new API key',
    'Text-to-Speech', 'Windows Update', 'ChatGPT Team', 'ChatGPT Business',
    'ChatGPT', 'Gmail', 'Outlook', 'Hotmail', 'OneCore', 'One Core',
    'Flash Lite', 'nbr-translate', 'authuser', 'AIza', 'tenant', 'Global',
    'Enable', 'Sign up', 'Top up', 'Service account name', 'API keys',
    'Create service account', 'Business', 'Team',
);

# A FEW VALUES ARE NOT A TRANSLITERATION OF THE LATIN ONE, and the language's
# own name is the first of them. Two rows reading "Srpski" and "Srpski" in
# Cyrillic are the SAME WORD to a screen reader -- a blind reader picking a
# language would hear it twice and have no way to tell them apart. Each names
# its own script instead.
my %override = (
    'LanguageName'      => "\x{421}\x{440}\x{43F}\x{441}\x{43A}\x{438} (\x{45B}\x{438}\x{440}\x{438}\x{43B}\x{438}\x{446}\x{430})",
    # THE LEGEND OVERRIDE IS GONE, and it was mine rather than the panel's.
    # Cyrillic really is wider than Latin -- "Biblioteka" is 76 units at 12 pt
    # Segoe and its transliteration 93 -- and the note here said the column is
    # 91, so "Knjige" was put on the key instead. But 91 is CLAUDE.md 8k's
    # DESIGN figure, written before the skin was built. The skin lays every
    # command key out at NewPlayerSkin.CellW, which is 108, and PaintLegends
    # hands DrawString that same rectangle with no inset. So 93 was never
    # anywhere near the edge, and the faithful transliteration fits with 15
    # units to spare. Read the constant, not the brief.
);

my ($in, $out) = @ARGV;
die "usage: sr-cyrillic.pl [--text] <input> <output>\n" unless $in && $out;

open(my $h, '<:raw', $in) or die "$in: $!\n";
my $text = decode('UTF-8', do { local $/; <$h> }); close $h;

# DECLARED SEPARATELY, and it has to be: a hash in a my LIST absorbs
# everything after it, so "my (%seen, $words, $digraphWords) = ((), 0, 0)"
# gave %seen the pair 0 => 0 and left both counters UNDEF. $words++ hid it
# (undef++ is 1), but a run where no word held a digraph printed an
# uninitialized-value warning over the summary line.
my %seen;
my $words = 0;
my $digraphWords = 0;

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

    # TWO KINDS OF INPUT, ONE SET OF RULES. A .lang file is key=value and only
    # the value converts; the manual (docs/help/sr.txt) is prose and nearly all
    # of it converts. Sharing the code rather than writing a second
    # transliterator is the whole point -- every lesson in the protection lists
    # above was paid for once, and a twin would drift away from them.
    my ($k, $v);
    if ($textMode) {
        next if $line =~ /^;/;                      # a comment in the source

        # The manual's own directives. LANG: is a code and must not convert at
        # all; the others carry prose that must. The markers -- #, ##, -, | and
        # {beta} -- are structure, so they are held out of the way exactly as a
        # key is, by splitting them off the front and putting them back after.
        if ($line =~ /^LANG:/) { next }
        if ($line =~ /^((?:TITLE|TOC|BETA|DIR):\s*)(.*)$/) { ($k, $v) = ($1, $2) }
        elsif ($line =~ /^((?:#{1,2}|-|\|)\s*)(.*)$/)      { ($k, $v) = ($1, $2) }
        else                                               { ($k, $v) = ('', $line) }
    }
    elsif ($line =~ /^([A-Za-z][A-Za-z0-9._]*=)(.*)$/s) {
        ($k, $v) = ($1, $2);
    }

    if (defined $v) {
        my @keep;
        # PHRASES FIRST, and the order is forced rather than chosen. @protect's
        # q/w/x/y rule works on single words, so run before it, it would lift
        # "key" out of "Create new secret key" and "keys" out of "API keys" and
        # leave the rest of each phrase to transliterate -- the same way it
        # broke Text-to-Speech. Longest first, so "ChatGPT Team" is taken whole
        # rather than being half-eaten by the bare "ChatGPT" behind it.
        for my $ph (sort { length($b) <=> length($a) } @phrases) {
            $v =~ s/\b\Q$ph\E\b/push @keep, $ph; "\x{FFFC}" . (scalar(@keep) - 1) . "\x{FFFD}"/ge;
        }
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
