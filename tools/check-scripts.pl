use strict; use warnings;
use Encode qw(decode);
binmode(STDOUT, ":utf8");

# Does any WORD mix two alphabets?
#
#   perl tools\check-scripts.pl "Nemoviz Book Reader\Lang\*.lang"
#
# WHY THIS EXISTS. Writing seven language files by hand means typing in Latin,
# Cyrillic and Greek in one sitting, and the three share letter SHAPES without
# sharing characters: Cyrillic е and Latin e are different characters that draw
# identically, as are а/a, о/o, р/p, с/c, х/x, and Greek ο/o, ν/v, ρ/p. One
# wrong keystroke gives a word that looks perfect on screen, passes every other
# check in this project -- the key is right, the placeholders are right, it fits
# its column -- and is read out as gibberish by a speech engine, because the
# engine sees a Croatian word with a Russian letter in the middle of it.
#
# Caught in the Esperanto file the day this was written: "sampreze" with a
# Cyrillic е. Nothing else would have found it.
#
# THE TEST IS THE WORD, NOT THE FILE, and that is deliberate. Every one of these
# files legitimately carries foreign script: a Russian file names Gemini and
# console.cloud.google.com in Latin, because those are things a reader has to
# type or click (the same reasoning as tools/sr-cyrillic.pl's protected runs).
# So a Latin word in a Cyrillic file is fine and a Cyrillic letter inside a
# Latin word is not. Splitting on non-letters and asking each word whether it
# is of one alphabet catches the second and ignores the first.

my @files = @ARGV;
die "usage: check-scripts.pl <lang file> [...]\n" unless @files;

my $bad = 0;
for my $path (@files) {
    open(my $h, '<:raw', $path) or die "$path: $!\n";
    my $text = decode('UTF-8', do { local $/; <$h> });
    close $h;

    my $line = 0;
    for my $l (split /\n/, $text, -1) {
        $line++;
        next unless $l =~ /^([A-Za-z][A-Za-z0-9._]*)=(.*)$/;
        my ($key, $value) = ($1, $2);

        # THE ESCAPES HAVE TO GO FIRST, and this tool walked straight into the
        # trap tools/sr-cyrillic.pl documents three files away. \n is a
        # BACKSLASH and a LETTER n; splitting on non-letters keeps the n and
        # glues it to the word after it, so every paragraph break in every
        # Cyrillic file came back as "nЕсли", "nВ", "nОна" -- a Latin letter in
        # a Cyrillic word, reported perfectly correctly and meaning nothing. 68
        # of the first run's 93 hits were this. They are removed rather than
        # split on, so the words either side stay separate.
        (my $scan = $value) =~ s/\\[rn]/ /g;

        # Split on anything that is not a letter, so punctuation, digits and
        # placeholders never join two words together.
        for my $word (split /[^\p{L}]+/, $scan) {
            next if length($word) < 2;
            my %script;
            for my $c (split //, $word) {
                $script{'Cyrillic'} = 1 if $c =~ /\p{Cyrillic}/;
                $script{'Greek'}    = 1 if $c =~ /\p{Greek}/;
                $script{'Latin'}    = 1 if $c =~ /\p{Latin}/;
            }
            next if keys(%script) < 2;

            # Name the odd letters out, since the word looks right by eye and
            # saying "this word is mixed" without saying WHERE helps nobody.
            my $minority = (sort { count($word, $a) <=> count($word, $b) } keys %script)[0];
            my @odd = grep { in_script($_, $minority) } split //, $word;
            printf "%s:%d  %s\n    %s  <- %s in a %s word: %s\n",
                $path, $line, $key, $word, $minority,
                join('+', sort grep { $_ ne $minority } keys %script),
                join(' ', map { sprintf "%s (U+%04X)", $_, ord($_) } @odd);
            $bad++;
        }
    }
}

print $bad ? "$bad mixed-alphabet word(s)\n" : "no word mixes two alphabets\n";
exit($bad ? 1 : 0);

sub in_script {
    my ($c, $s) = @_;
    return $c =~ /\p{Cyrillic}/ if $s eq 'Cyrillic';
    return $c =~ /\p{Greek}/    if $s eq 'Greek';
    return $c =~ /\p{Latin}/;
}

sub count {
    my ($word, $s) = @_;
    my $n = 0;
    for my $c (split //, $word) { $n++ if in_script($c, $s) }
    return $n;
}
