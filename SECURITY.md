# Security Policy

## Reporting

Use GitHub private vulnerability reporting:
https://github.com/utajum/g-helper-linux/security/advisories/new

Include affected version, laptop model, and reproduction steps.
Please do not open public issues for suspected vulnerabilities.

Only the latest release is supported. Fixes land on master and ship in
the next release; older versions are not patched.

## What this project is

G-Helper for Linux is a single-user desktop utility for ASUS, Lenovo and other laptops.
Its purpose is privileged hardware control: GPU power and PCI
bind/unbind, embedded controller writes, MSR access, fan curves, TDP
limits, keyboard remapping.

To do this it installs root-owned helpers in /usr/local/lib/ghelper
and a passwordless sudoers rule for them. That is the documented
design, not a defect. The application cannot perform its function
without root access to hardware interfaces.

## Threat model

The local desktop user is trusted. They own the machine, installed the
tool, and are its intended operator.

Trusted:

- the local desktop user and their session
- root and anything already running as root
- the kernel, firmware, and ASUS WMI interfaces
- system package managers and the distro toolchain

Untrusted:

- remote network input
- release artifacts and update channels
- untrusted file paths or arguments originating outside the app

## In scope

- Any issue exploitable without an existing local session.
- Arbitrary code execution as root through a helper binary, including
  command injection, argument injection, and unsafe exec of
  caller-controlled paths.
- Writes or reads outside the fixed set of paths a helper subcommand is
  documented to touch, for example path traversal or symlink attacks.
- Privilege retention: anything that leaves persistent root access
  behind after the app exits or is uninstalled.
- Tampering with install, update, or release verification.
- Credential or token disclosure.

## Out of scope

- Actions available to a user who already has an active local session.
  This includes local denial of service, killing or signalling
  processes, unbinding or power-cycling devices, unloading modules, and
  changing power limits.
- Tampering with files, processes or settings the local user owns, for
  example the ghelper binary in /opt/ghelper, ~/.config/ghelper, cache
  or autostart entries. A compromised user account is already fully
  compromised. Reports are in scope only if they reach root without
  the user's authentication.
- The existence of the root-owned helpers or the passwordless sudoers
  rule that invokes them. Both are required by design and documented
  above.
- Hardware being reachable at all, or the risk of hardware misuse
  through documented features.
- Physical access attacks.
- Anything requiring root to begin with.
- Instability, crashes, or data loss from unsupported hardware,
  overclocking, undervolting, or manual fan curves. Report those as
  normal bugs.

Reports falling under out of scope will be closed as such. CVE
assignment is declined for them.

## Handling

Acknowledged within 7 days. In-scope reports get a fix timeline on
triage and credit in the advisory and CHANGELOG unless you prefer
otherwise. Please allow a fix to ship before public disclosure.
