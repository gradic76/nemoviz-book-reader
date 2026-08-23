use strict; use warnings;
use Encode qw(decode encode);
binmode(STDOUT, ':utf8');

# Builds the manual: docs/help/<code>.txt  ->  Nemoviz Book Reader/Help/<code>/index.html
#
#   perl tools\make-help.pl
#
# WHY A SOURCE FILE AND A GENERATOR, rather than eleven hand-written HTMLs.
# The manual exists in every language NBR is localized into, and a correction to
# one paragraph would otherwise mean editing eleven files by hand and getting it
# right in all of them. One template means the page STRUCTURE cannot drift
# between languages while the words differ, which is the same reason the .lang
# files are ordered by tools/lang-order.txt.
#
# docs/NBR.docx IS STILL GORDAN'S, and it is the original of the Croatian text.
# docs/help/hr.txt is derived from it, with the corrections of 2026-08-23
# applied. If he edits the docx again, re-derive hr.txt from it rather than
# letting the two drift; if he edits hr.txt, the docx is the one that is stale.
# Nothing here ever writes to the docx.
#
# THE MARKUP IS DELIBERATELY TINY, because a translator has to be able to work
# in it without learning anything:
#
#   # Heading          a chapter          -> <h1>
#   ## Heading         a section          -> <h2>
#   - item             a bullet           -> <li>
#   | Key | What       a shortcut row     -> <tr> in a table
#   anything else      a paragraph        -> <p>
#   blank line         separates blocks
#   ; comment          dropped
#
# A heading may carry a trailing {beta} marker. That prints the "not in this
# beta" note under it, in the language's own words, taken from the file's own
# BETA: line -- so the warning cannot be in English inside a Greek manual.

my $root = $ARGV[0] || ".";
my %shape;
my $src  = "$root/docs/help";
my $out  = "$root/Nemoviz Book Reader/Help";

opendir(my $d, $src) or die "$src: $!\n";
my @codes = sort map { /^(.+)\.txt$/ ? $1 : () } readdir $d;
closedir $d;
die "no source files in $src\n" unless @codes;

for my $code (@codes) {
    open(my $h, '<:raw', "$src/$code.txt") or die "$src/$code.txt: $!\n";
    my $text = decode('UTF-8', do { local $/; <$h> });
    close $h;

    my (%meta, @body);
    for my $line (split /\n/, $text, -1) {
        $line =~ s/\r$//;
        if ($line =~ /^(TITLE|LANG|DIR|BETA|TOC):\s*(.*)$/) { $meta{$1} = $2; next }
        next if $line =~ /^;/;
        push @body, $line;
    }
    $meta{TITLE} ||= 'Nemoviz Book Reader';
    $meta{LANG}  ||= $code;
    $meta{BETA}  ||= 'Not in this beta.';
    $meta{TOC}   ||= 'Contents';

    my (@html, @toc, $inList, $inTable);
    my $hn = 0;

    my $closeBlocks = sub {
        if ($inList)  { push @html, "</ul>";    $inList  = 0 }
        if ($inTable) { push @html, "</table>"; $inTable = 0 }
    };

    for my $line (@body) {
        if ($line =~ /^\s*$/) { $closeBlocks->(); next }

        # ---- headings, with an optional {beta} marker
        if ($line =~ /^(#{1,2})\s+(.*)$/) {
            my ($hashes, $title) = ($1, $2);
            $closeBlocks->();
            my $beta = ($title =~ s/\s*\{beta\}\s*$//) ? 1 : 0;
            my $level = length($hashes);
            my $id = "s" . (++$hn);
            push @html, sprintf('<h%d id="%s">%s</h%d>', $level, $id, esc($title), $level);
            push @html, '<p class="beta">' . esc($meta{BETA}) . '</p>' if $beta;
            push @toc, { level => $level, id => $id, title => $title };
            next;
        }

        # ---- a shortcut table row:  | Space | Play or pause
        # A CLOSING PIPE IS ALLOWED and stripped, because that is how anyone who
        # has met a table in markdown will write the row, and the first version
        # of this quietly put the stray "|" inside the second cell instead.
        if ($line =~ /^\|/) {
            (my $row = $line) =~ s/^\|\s*//;
            $row =~ s/\s*\|\s*$//;
            my ($key, $what) = split /\s*\|\s*/, $row, 2;
            $what = '' unless defined $what;
            $closeBlocks->() if $inList;
            push @html, '<table class="keys">' unless $inTable;
            $inTable = 1;
            push @html, "<tr><th>" . esc($key) . "</th><td>" . esc($what) . "</td></tr>";
            next;
        }

        # ---- a bullet
        if ($line =~ /^-\s+(.*)$/) {
            $closeBlocks->() if $inTable;
            push @html, "<ul>" unless $inList;
            $inList = 1;
            push @html, "<li>" . esc($1) . "</li>";
            next;
        }

        $closeBlocks->();
        push @html, "<p>" . esc($line) . "</p>";
    }
    $closeBlocks->();

    # ---- contents. Every chapter, and its sections nested under it. A manual
    # read by ear is walked by heading, so the list is the map a reader gets
    # before they commit to scrolling.
    # A chapter with no sections under it gets NO nested list. An empty <ul> is
    # invisible on screen and is not invisible to a screen reader, which
    # announces a list with no items -- so it has to be decided by looking
    # AHEAD at whether a section follows, not by opening one and hoping.
    my @nav;
    my $open = 0;
    for my $i (0 .. $#toc) {
        my $t = $toc[$i];
        if ($t->{level} == 1) {
            push @nav, "</ul></li>" if $open;
            my $hasKids = ($i < $#toc && $toc[$i + 1]{level} == 2);
            push @nav, sprintf('<li><a href="#%s">%s</a>%s',
                               $t->{id}, esc($t->{title}), $hasKids ? '<ul>' : '</li>');
            $open = $hasKids;
        } else {
            push @nav, sprintf('<li><a href="#%s">%s</a></li>', $t->{id}, esc($t->{title}));
        }
    }
    push @nav, "</ul></li>" if $open;

    my $page = page($meta{LANG}, $meta{TITLE}, $meta{TOC},
                    join("\n", @nav), join("\n", @html));

    my $dir = "$out/$code";
    mkdir $out unless -d $out;
    mkdir $dir unless -d $dir;
    open(my $o, '>:raw', "$dir/index.html") or die "$dir/index.html: $!\n";
    print $o encode('UTF-8', $page);
    close $o;

    printf "%-8s %3d headings, %5d words -> Help/%s/index.html\n",
        $code, scalar(@toc), scalar(split /\s+/, join(' ', @body)), $code;

    # The SHAPE of the page, for the comparison below.
    $shape{$code} = join ' ', map { block($_) } grep { !/^\s*$/ } @body;
}

# EVERY LANGUAGE MUST HAVE THE SAME SHAPE AS THE SOURCE, and this is the check
# that makes translating a manual into nine languages survivable. A translator
# -- or I, writing them -- drops a paragraph, merges two, or turns a bullet into
# prose, and nothing else would ever say so: the page still builds, still reads,
# and is quietly missing a sentence that the Croatian one has. Comparing the
# sequence of block KINDS (heading level, bullet, table row, paragraph) catches
# exactly that while saying nothing about the words, which are of course meant
# to differ.
my $base = exists $shape{hr} ? 'hr' : $codes[0];
my $bad = 0;
for my $code (sort keys %shape) {
    next if $code eq $base;
    next if $shape{$code} eq $shape{$base};
    my @a = split ' ', $shape{$base};
    my @b = split ' ', $shape{$code};
    my $i = 0;
    $i++ while $i < @a && $i < @b && $a[$i] eq $b[$i];
    printf "  %s: shape differs from %s at block %d - %s has %s, %s has %s\n",
        $code, $base, $i + 1, $base, ($a[$i] // '(end)'), $code, ($b[$i] // '(end)');
    $bad++;
}
print $bad ? "$bad language(s) differ in shape from $base\n"
           : "every language has the same shape as $base\n";

sub block {
    my $l = shift;
    # The {beta} marker is PART OF THE SHAPE, not decoration. Without it here a
    # heading that lost its marker still counted as a heading and the check
    # passed -- which is exactly what happened to the Cyrillic manual, where the
    # transliteration turned {beta} into {Cyrillic beta} and the one page that
    # had to warn the reader quietly stopped warning them.
    return "h" . length($1) . ($l =~ /\{beta\}/ ? "b" : "") if $l =~ /^(#{1,2})\s/;
    return "row"            if $l =~ /^\|/;
    return "li"             if $l =~ /^-\s/;
    return "meta"           if $l =~ /^(TITLE|LANG|DIR|BETA|TOC):/;
    return "p";
}

sub esc {
    my $s = shift;
    $s =~ s/&/&amp;/g; $s =~ s/</&lt;/g; $s =~ s/>/&gt;/g;
    # A run in *stars* is emphasis. The only markup inside a line, and it is
    # here because a manual needs to be able to say DO NOT without shouting.
    $s =~ s{\*([^*]+)\*}{<strong>$1</strong>}g;
    return $s;
}

# THE STYLING IS RESTRAINED ON PURPOSE. This page opens in the reader's own
# browser precisely so that their fonts, colours and screen reader settings
# apply (HintSystem.OpenManual). So it sets a comfortable measure and leaves
# colour alone except where meaning depends on it -- and it follows the
# browser's dark mode rather than forcing a background.
sub page {
    my ($lang, $title, $tocLabel, $nav, $body) = @_;
    return <<"HTML";
<!DOCTYPE html>
<html lang="$lang">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$title</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: Georgia, "Times New Roman", serif; font-size: 1.1em;
         line-height: 1.6; max-width: 42em; margin: 2em auto; padding: 0 1.5em; }
  h1 { font-size: 1.5em; margin-top: 2em; }
  h2 { font-size: 1.2em; margin-top: 1.6em; }
  p, li { margin: 0.8em 0; }
  nav ul { list-style: none; padding-left: 1em; }
  nav > ul { padding-left: 0; }
  nav li { margin: 0.3em 0; }
  table.keys { border-collapse: collapse; margin: 1em 0; }
  table.keys th, table.keys td { text-align: left; vertical-align: top;
         padding: 0.3em 1.2em 0.3em 0; font-weight: normal; }
  table.keys th { white-space: nowrap; font-family: inherit; }
  .beta { border-left: 4px solid currentColor; padding-left: 0.8em;
          font-style: italic; opacity: 0.85; }
</style>
</head>
<body>

<h1>$title</h1>

<nav aria-label="$tocLabel">
<h2>$tocLabel</h2>
<ul>
$nav
</ul>
</nav>

$body

</body>
</html>
HTML
}
