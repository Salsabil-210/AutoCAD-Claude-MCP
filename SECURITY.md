# Security Policy

## Supported versions

Only the latest published release is supported with security updates.

## Reporting a vulnerability

Please do not disclose vulnerabilities, tokens, private drawings, or proof-of-concept exploits in a public issue.

Use GitHub's **Report a vulnerability** option in the repository Security tab. If private vulnerability reporting is not available, contact the repository owner privately before sharing technical details.

Include the affected version, operating system, AutoCAD version, reproduction steps, impact, and any suggested mitigation. Remove customer names, proprietary geometry, file paths, and other sensitive information from screenshots and logs.

## Security boundaries

The bridge is intended for same-computer communication only. It binds to `127.0.0.1`, uses a per-install random token, exposes an explicit tool allowlist, and validates tool arguments. These controls reduce risk but do not make an unsigned prototype suitable for unreviewed production deployment.

Do not add arbitrary shell execution, unrestricted AutoCAD command execution, dynamic AutoLISP evaluation, or remote-network listening. New write tools should support clear user approval and a practical undo path.
