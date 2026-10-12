# Contributing

Thank you for helping improve AutoCAD Claude Bridge.

## Before opening a pull request

1. Open an issue for substantial features or protocol changes.
2. Keep tools narrow and schema-driven.
3. Validate every external input before calling the AutoCAD API.
4. Perform AutoCAD API work on the main thread.
5. Do not add arbitrary command, script, shell, or filesystem execution.
6. Preserve the local-only listener and per-install token model.
7. Build the release configuration and test against a disposable drawing.
8. Update the README and documentation when behavior changes.

## Development setup

Install AutoCAD 2027, the .NET 10 SDK, and PowerShell. Then run:

```powershell
.\build.ps1
.\package.ps1
```

Use `NETLOAD` with the generated DLL for development testing. Use `CLAUDEBRIDGE` and `autocad_status` before testing write tools.

## Pull requests

Explain the user problem, the safety impact, how the change was tested, and how users can reverse any drawing modification. Do not include Autodesk binaries, user DWG files, generated `bin`, `obj`, or `dist` folders, access tokens, credentials, or personal data.

By contributing, you agree that your contribution is licensed under the project's MIT License.
