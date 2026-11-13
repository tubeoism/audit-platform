# Bundled password list

`common-passwords.txt.gz` holds 46,816 common passwords of 12 to 64 characters, lower-cased, one per line.
It is the offline part of the breached-password check (ADR-0005): the master refuses these passwords even when
haveibeenpwned.com cannot be reached.

Source: [SecLists](https://github.com/danielmiessler/SecLists) `Passwords/Common-Credentials`
(`100k-most-used-passwords-NCSC.txt` and `xato-net-10-million-passwords-1000000.txt`), MIT licence,
copyright (c) 2018 Daniel Miessler. Filtered to printable ASCII, length 12-64, de-duplicated.
