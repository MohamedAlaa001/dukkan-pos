# Contributing to Dukkan

Bug reports, documentation improvements and pull requests are welcome.

## Report a problem

Use the bug report template and include the version shown on the home screen,
your Windows version, reproduction steps, and what you expected to happen.
Screenshots and sanitized logs help. Do not upload shop databases, customer
records, credentials, recovery codes or backup secrets.

For security vulnerabilities, follow [SECURITY.md](SECURITY.md).

## Propose a change

For substantial changes, open a feature request first so the scope can be
discussed. Keep pull requests focused and explain the problem, resulting
behavior and verification performed. Treat other contributors respectfully.

## Build and verify

Use Windows and the .NET 8 SDK. See [the development guide](docs/DEVELOPMENT.md)
for project structure and isolated UI checks.

```powershell
dotnet restore Dukkan.sln
dotnet build Dukkan.sln --no-restore
dotnet test Dukkan.sln --no-restore --nologo -v minimal
```

Use disposable demo data. Exercise affected workflows in the actual app,
including English and Arabic layouts for UI changes. Include regression tests
for business logic and persistence fixes. Preserve posted history, permissions,
idempotency and migration safety; never use a real shop database for testing.

## License

By submitting a contribution, you agree that it may be distributed under the
project's [MIT license](LICENSE). Only submit work you have permission to share.
Third-party components retain their own licenses and notices.
