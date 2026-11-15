# Changelog

## Unreleased — Authnet public-key support

### Added

- Authnet password-proof public-key setting in the launcher configuration.
- Validation for 128-byte, little-endian public keys encoded as 256 hex characters; whitespace is accepted.
- Regression checks for malformed keys, invalid group elements, module checksums, and preservation of module verification code.

### Changed

- Login hostnames are resolved to IPv4 before starting the client, fixing "An invalid IP address was specified" for domain names. DNS failures and IPv6-only targets now produce clear errors.
- Authentication modules now trust the configured server public key while retaining the original server-proof verification branch, replacing the previous branch inversion patch.
- Recognized older password modules are backed up outside the shared Battle.net cache before removal from that cache to avoid using a previous server key.
- Pristine authentication modules can also be recovered from the launcher's module backup directory.
- The configuration panel scrolls to keep its controls accessible.
- Setup documentation explains public-key configuration, cache backups, and regression checks.

### Upgrade notes

- Authnet users must enter their server administrator's password-proof public key before launching. Standard login does not require a key.
- Close existing game clients before changing server keys. Changes take effect on the next client launch.
- Private keys and user-specific server configuration are not included in the repository.

### Validation

- Windows Release build and self-contained publish passed.
- Key-validation checks and a real 64-bit build 18414 module check passed; only the public-key bytes changed.
- End-to-end production login and the real 32-bit module remain unverified.
