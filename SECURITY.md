# Security policy

## Supported versions

Security fixes are released for the latest minor version. Older versions are not patched; update to the latest release.

| Version | Supported |
|---|---|
| 1.0.x | yes |
| < 1.0 | no |

## Reporting a vulnerability

Please **do not** open a public issue for a security problem.

Report it privately through GitHub security advisories:
[Report a vulnerability](https://github.com/maut-dev/maut-vision/security/advisories/new) (the "Security" tab of the repository →
"Report a vulnerability").

Include:

- the affected version (`Version` in the installer name or the application log) and the Windows version;
- a description of the problem and its impact;
- steps to reproduce or a proof of concept;
- any known workaround.

You will get an acknowledgement within 7 days. We will investigate, keep you informed about progress, and agree on a disclosure date once a
fix is available. Credit is given in the advisory and the changelog unless you prefer to stay anonymous.

## Scope

In scope: the desktop application, its installer, the bundled Python worker and the handling of project files and archives (for example,
path traversal in imported archives, unsafe file handling, code execution through project data).

Out of scope: vulnerabilities of third-party components that are already fixed upstream and only need a version update (please still tell
us), and issues that require an attacker with full control of the user's account.
