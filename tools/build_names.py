"""Builds src/HanYeong.Core/Data/names.txt.gz: personal names and place names that the word lists miss.

A capitalized word in this list is left as English by the auto mode (Drua, Ndidi, Theckanath are not
Korean typed in the wrong mode). Sources (see tools/DICTIONARIES.md):
  wikidata-given.tsv, wikidata-family.tsv  Wikidata labels of given/family names (CC0), QLever tsv_export
  census/Names_2010Census.csv              US Census 2010 surnames (public domain)
  geonames/cities1000.txt                  GeoNames cities with 1,000+ people (CC BY 4.0)

Usage: python tools/build_names.py <download-folder>
"""
import gzip
import os
import re
import sys

root = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
data = os.path.join(here, '..', 'src', 'HanYeong.Core', 'Data')

# One word of Latin letters, capitalized as a name is (McVeigh, DeShawn allowed); 4+ letters, as 2-3 letter
# names collide with short Korean typed in English mode far too often.
NAME = re.compile(r'^[A-Z][a-z]+(?:[A-Z][a-z]+)?$')

names = set()


def add(raw):
    raw = raw.strip()
    if len(raw) >= 4 and NAME.match(raw):
        names.add(raw.lower())


for kind in ('given', 'family'):
    with open(os.path.join(root, f'wikidata-{kind}.tsv'), encoding='utf-8') as f:
        next(f)
        for line in f:
            m = re.match(r'^"(.*)"@\w+$', line.strip())
            if m:
                add(m.group(1))

with open(os.path.join(root, 'census', 'Names_2010Census.csv'), encoding='utf-8') as f:
    next(f)
    for line in f:
        add(line.split(',')[0].title())

with open(os.path.join(root, 'geonames', 'cities1000.txt'), encoding='utf-8') as f:
    for line in f:
        add(line.split('\t')[2])   # asciiname

# Common English words (score 3.0+: Will, Rose, Grant) are judged by their frequency; rarer listed ones
# (drua 1.9, ndidi 1.6) are where a name list helps.
english = set()
with gzip.open(os.path.join(data, 'en.tsv.gz'), 'rt', encoding='utf-8') as f:
    for line in f:
        word, score = line.rstrip('\n').split('\t')
        if float(score) >= 3.0:
            english.add(word)
names -= english

with gzip.open(os.path.join(data, 'names.txt.gz'), 'wt', encoding='utf-8', compresslevel=9) as f:
    for name in sorted(names):
        f.write(name + '\n')
print(f'{len(names)} names')
