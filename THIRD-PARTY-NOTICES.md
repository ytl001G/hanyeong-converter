# Third-party notices

## Word-frequency data

`src/HanYeong.Core/Data/en.tsv.gz` and `ko.tsv.gz` (embedded in HanYeong.exe) are derived works:
word lists merged and reduced to a single frequency score per word by `tools/build_dictionaries.py`.
Because some sources are share-alike, **these two files are licensed under
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)**.

Sources and their licenses:

| Source | License |
|---|---|
| ENABLE word list (via github.com/dolph/dictionary) | Public domain |
| Hunspell en_US dictionary, based on SCOWL by Kevin Atkinson (via LibreOffice/dictionaries) | SCOWL license (BSD-style, permissive; copyright notices retained in the original distribution) |
| FrequencyWords, OpenSubtitles 2018 English and Korean lists, by Hermit Dave (github.com/hermitdave/FrequencyWords) | CC BY-SA 4.0 |
| Wikipedia word frequency, English Wikipedia 2023-04-13, by Ilya Semenov (github.com/IlyaSemenov/wikipedia-word-frequency); text © Wikipedia contributors | CC BY-SA |
| Leipzig Corpora Collection: kor_news_2022_300K, kor_wikipedia_2021_300K (wortschatz.uni-leipzig.de) | CC BY 4.0 |

Leipzig citation: D. Goldhahn, T. Eckart & U. Quasthoff: *Building Large Monolingual Dictionaries at the
Leipzig Corpora Collection: From 100 to 200 Languages.* Proceedings of LREC 2012.

Hand-picked additions (`Data/extra-words*.txt`) are part of this project.

## Runtime

The standalone build bundles the .NET runtime (© .NET Foundation and contributors, MIT License).
