"""Builds the word-frequency tables the auto mode uses.

Each source is normalised to a Zipf-like score: log10(occurrences per billion words).
~1 = rare, ~3 = ordinary, 6+ = very common. A word's score is the max over its sources;
list-only sources (no counts) give a fixed base score.

Usage: python tools/build_dictionaries.py <download-dir>
Download dir layout (see tools/DICTIONARIES.md for URLs):
  enable1.txt, en_US.dic, en_50k.txt, enwiki.txt, ko_full.txt,
  kor_news_2022_300K/*-words.txt, kor_wikipedia_2021_300K/*-words.txt
"""
import glob
import gzip
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, 'src', 'HanYeong.Core', 'Data')

LATIN = re.compile(r'^[a-z]{2,}$')
HANGUL = re.compile(r'^[가-힣]+$')

BASE_LIST_ONLY = 1.0   # in a dictionary, frequency unknown
BASE_CURATED = 6.5     # hand-picked: treated as very common so they are never "corrected"


def zipf(count, total):
    return math.log10(count / total * 1e9)


def read_counts(path, word_col, count_col, sep=None, min_count=1):
    rows = []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            parts = line.rstrip('\n').split(sep)
            if len(parts) <= max(word_col, count_col):
                continue
            try:
                rows.append((parts[word_col], int(parts[count_col])))
            except ValueError:
                continue
    total = sum(c for _, c in rows)
    return [(w, zipf(c, total)) for w, c in rows if c >= min_count]


def merge(table, entries, accept, normalise=lambda w: w):
    added = 0
    for word, score in entries:
        word = normalise(word)
        if not accept(word):
            continue
        if score > table.get(word, -1):
            if word not in table:
                added += 1
            table[word] = score
    return added


def list_only(path, score, strip_flags=False):
    out = []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            word = line.strip()
            if strip_flags:
                word = word.split('/')[0]
            if word and not word.startswith('#'):
                out.append((word, score))
    return out


def write(table, name):
    path = os.path.join(DATA, name)
    with gzip.open(path, 'wt', encoding='utf-8', compresslevel=9) as f:
        for word in sorted(table):
            f.write(f'{word}\t{table[word]:.1f}\n')
    print(f'{name}: {len(table):,} words, {os.path.getsize(path):,} bytes')


def main(dl):
    en = {}
    lower = str.lower
    is_latin = LATIN.match
    print('en  ENABLE      +', merge(en, list_only(os.path.join(dl, 'enable1.txt'), BASE_LIST_ONLY), is_latin, lower))
    # Hunspell's first line is the entry count; flags after '/' are affix rules.
    print('en  hunspell    +', merge(en, list_only(os.path.join(dl, 'en_US.dic'), BASE_LIST_ONLY, strip_flags=True), is_latin, lower))
    print('en  subtitles   +', merge(en, read_counts(os.path.join(dl, 'en_50k.txt'), 0, 1), is_latin, lower))
    print('en  wikipedia   +', merge(en, read_counts(os.path.join(dl, 'enwiki.txt'), 0, 1, min_count=50), is_latin, lower))
    print('en  curated     +', merge(en, list_only(os.path.join(DATA, 'extra-words.txt'), BASE_CURATED), is_latin, lower))

    ko = {}
    is_hangul = HANGUL.match
    print('ko  subtitles   +', merge(ko, read_counts(os.path.join(dl, 'ko_full.txt'), 0, 1, min_count=2), is_hangul))
    for corpus in ('kor_news_2022_300K', 'kor_wikipedia_2021_300K'):
        for path in glob.glob(os.path.join(dl, corpus, '**', '*-words.txt'), recursive=True):
            # Leipzig format: id <TAB> word <TAB> frequency
            print(f'ko  {corpus} +', merge(ko, read_counts(path, 1, 2, sep='\t', min_count=2), is_hangul))
    print('ko  curated     +', merge(ko, list_only(os.path.join(DATA, 'extra-words-ko.txt'), BASE_CURATED), is_hangul))

    write(en, 'en.tsv.gz')
    write(ko, 'ko.tsv.gz')


if __name__ == '__main__':
    main(sys.argv[1])
