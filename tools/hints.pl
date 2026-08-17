use strict; use warnings;

# Every help hint, out of en.lang into one readable document -- and back again.
#
#   perl tools\hints.pl <en.lang>                  > "docs\Help hints.txt"
#   perl tools\hints.pl <en.lang> --import "docs\Help hints.txt"
#
# THE ROUND TRIP IS SAFE BECAUSE THE HINTS SAY SO, not because it was hoped.
# Measured before it was written: every line break in every hint is a PAIR
# (\n\n) and not one of the 32 uses a lone \n. So in the document a blank line
# means a paragraph break and two lines next to each other mean nothing but
# wrapping -- which is exactly what the reverse needs to know. If a lone \n is
# ever added to a hint, this stops being reversible and the check below will
# say so rather than quietly mangling it.
#
# en.lang stays the single source of truth (CLAUDE.md section 3). The import
# only ever REPLACES the value of a hint key that already exists; it never adds,
# removes or reorders a line, and everything else in the file passes through
# byte for byte.

my %where = (
    'Hint.SoundProcessing'      => 'Properties - sound',
    'Hint.RemoveRumble'         => 'Properties - sound',
    'Hint.NoiseRemoval'         => 'Properties - sound',
    'Hint.SoftenSibilance'      => 'Properties - sound',
    'Hint.EvenOutSpeech'        => 'Properties - sound',
    'Hint.Tone'                 => 'Properties - sound',
    'Hint.AutomaticLoudness'    => 'Properties - sound',
    'Hint.Text0'                => 'Properties - reading',
    'Hint.TextBrailleSource'    => 'Properties - reading',
    'Hint.TextVisual'           => 'Properties - reading',
);
my %order = ('Player and dialogs' => 1, 'Properties - sound' => 2,
             'Properties - reading' => 3, 'Settings' => 4);

my $lang = shift @ARGV or die "usage: hints.pl <en.lang> [--import <doc>]\n";
my $mode = shift @ARGV || '';
my $doc  = shift @ARGV || '';

sub is_hint {
    my $k = shift;
    return $k =~ /^[A-Za-z0-9._]*[Hh]int[A-Za-z0-9._]*$/ && $k !~ /Accessible|ShowHints/;
}

open(my $in, '<:raw', $lang) or die "cannot read $lang: $!\n";
my @lines = <$in>;
close $in;

if ($mode ne '--import') { export(); exit 0; }
import_doc();
exit 0;

# ---------------------------------------------------------------- export

sub export {
    my (%bag, $n);
    for my $line (@lines) {
        next unless $line =~ /^([A-Za-z0-9._]+)=(.*?)\r?\n?$/;
        my ($k, $v) = ($1, $2);
        next unless is_hint($k);
        my $g = $where{$k} || ($k =~ /^(Settings\.|Hint\.Settings)/ ? 'Settings'
                                                                   : 'Player and dialogs');
        push @{ $bag{$g} }, [$k, $v];
        $n++;
    }

    binmode(STDOUT, ':raw');
    print "NEMOVIZ BOOK READER - every help hint, in one place\n";
    print "=" x 70, "\n\n";
    print <<"HEAD";
$n hints, grouped by the window they appear in.

YOU CAN EDIT THIS FILE. Change the wording under any heading and then write it
back into the language file with:

    perl tools\\hints.pl "Nemoviz Book Reader\\Lang\\en.lang" --import "docs\\Help hints.txt"

Leave the KEY LINES alone -- they are how a hint is found. Inside a hint, a
blank line starts a new paragraph and everything else is just wrapping, so you
need not keep the line lengths tidy. Nothing else in the language file is
touched, and git holds the previous version either way.
HEAD

    for my $g (sort { ($order{$a}||9) <=> ($order{$b}||9) } keys %bag) {
        print "\n\n", "-" x 70, "\n", uc($g), "\n", "-" x 70, "\n";
        for my $e (@{ $bag{$g} }) {
            my ($k, $v) = @$e;
            $v =~ s/\x5cn/\n/g;
            print "\n$k\n\n";
            for my $para (split /\n/, $v, -1) {
                if ($para =~ /^\s*$/) { print "\n"; next; }
                my $out = '';
                for my $w (split /\s+/, $para) {
                    if (length($out) + length($w) + 1 > 78) { print "$out\n"; $out = $w; }
                    else { $out = length($out) ? "$out $w" : $w; }
                }
                print "$out\n";
            }
        }
    }
}

# ---------------------------------------------------------------- import

sub import_doc {
    open(my $d, '<:raw', $doc) or die "cannot read $doc: $!\n";
    my @doc = <$d>;
    close $d;

    # Which keys the language file actually has, so a typo in the document is
    # reported rather than silently ignored.
    my %known;
    for my $line (@lines) {
        $known{$1} = 1 if $line =~ /^([A-Za-z0-9._]+)=/ && is_hint($1);
    }

    my (%text, $cur, @para, @buf);
    my $flush = sub {
        return unless defined $cur;
        push @para, join(' ', @buf) if @buf;
        $text{$cur} = join("\x5cn\x5cn", @para);
        ($cur, @para, @buf) = (undef, (), ());
    };

    for my $raw (@doc) {
        my $line = $raw; $line =~ s/\r?\n$//;
        if ($line =~ /^[-=]{10,}$/) { $flush->(); next; }        # a rule
        if ($line =~ /^([A-Za-z0-9._]+)$/ && ($known{$1} || is_hint($1))) {
            $flush->(); $cur = $1; next;
        }
        next unless defined $cur;
        if ($line =~ /^\s*$/) { push(@para, join(' ', @buf)) if @buf; @buf = (); next; }
        push @buf, $line;
    }
    $flush->();

    my ($changed, $same, $unknown) = (0, 0, 0);
    for my $k (sort keys %text) {
        unless ($known{$k}) { print "  NOT IN en.lang, skipped: $k\n"; $unknown++; delete $text{$k}; }
    }

    for my $line (@lines) {
        next unless $line =~ /^([A-Za-z0-9._]+)=(.*?)(\r?\n)?$/;
        my ($k, $old, $eol) = ($1, $2, $3 || "\r\n");
        next unless exists $text{$k};
        if ($text{$k} eq $old) { $same++; next; }
        $line = "$k=$text{$k}$eol";
        $changed++;
        print "  changed: $k\n";
    }

    if ($changed) {
        open(my $out, '>:raw', $lang) or die "cannot write $lang: $!\n";
        print $out @lines;
        close $out;
    }
    printf("\n%d changed, %d unchanged, %d not in en.lang.%s\n",
           $changed, $same, $unknown, $changed ? "" : " Nothing written.");
}
