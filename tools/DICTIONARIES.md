# Word-frequency data

The auto mode compares how common a word is in English vs Korean. `tools/build_dictionaries.py`
merges the sources below into `src/HanYeong.Core/Data/en.tsv.gz` and `ko.tsv.gz` (word, Zipf-like score).

## Sources

| File | Source | What it adds | License |
|---|---|---|---|
| `enable1.txt` | ENABLE word list — https://raw.githubusercontent.com/dolph/dictionary/master/enable1.txt | 173k English words incl. inflections | Public domain |
| `en_US.dic` | Hunspell en_US (SCOWL), LibreOffice — https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic | Proper nouns, abbreviations | SCOWL/BSD-style |
| `en_50k.txt` | OpenSubtitles 2018 frequencies (hermitdave/FrequencyWords) — https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt | Conversational frequency, slang | CC BY-SA 4.0 |
| `enwiki.txt` | English Wikipedia frequencies (IlyaSemenov/wikipedia-word-frequency) — https://raw.githubusercontent.com/IlyaSemenov/wikipedia-word-frequency/master/results/enwiki-2023-04-13.txt | Written frequency, names, technical terms | MIT (code), CC BY-SA (Wikipedia text) |
| `ko_full.txt` | OpenSubtitles 2018 Korean (hermitdave/FrequencyWords) — https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/ko/ko_full.txt | Conversational Korean eojeol frequency | CC BY-SA 4.0 |
| `kor_news_2022_300K/` | Leipzig Corpora Collection — https://downloads.wortschatz-leipzig.de/corpora/kor_news_2022_300K.tar.gz | News Korean | CC BY 4.0 |
| `kor_wikipedia_2021_300K/` | Leipzig Corpora Collection — https://downloads.wortschatz-leipzig.de/corpora/kor_wikipedia_2021_300K.tar.gz | Encyclopedic Korean | CC BY 4.0 |
| `Data/extra-words.txt`, `Data/extra-words-ko.txt` | Hand-picked (this repo) | Chat slang, brands, places, tech terms | — |

The CC BY-SA / CC BY sources require attribution if the app is distributed.

## Rebuild

Download the files above into one folder (Leipzig archives extracted into their own subfolders), then:

```
python tools/build_dictionaries.py <download-folder>
```
