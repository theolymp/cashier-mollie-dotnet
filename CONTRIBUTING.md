# Contributing to CashierMollie for .NET

Thank you for considering contributing! This project aims to bring feature parity with [laravel/cashier-mollie](https://github.com/laravel/cashier-mollie) to the .NET ecosystem.

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/YOUR_USERNAME/cashier-mollie-dotnet.git`
3. Create a branch: `git checkout -b feature/my-feature`
4. Install .NET 10 SDK
5. Build: `dotnet build`
6. Test: `dotnet test`

## Development

### Prerequisites

- .NET 10 SDK
- An IDE with C# support (VS Code, Rider, Visual Studio)

### Code Style

- Follow the `.editorconfig` rules in the repository
- `TreatWarningsAsErrors` is enabled — the build must produce 0 warnings
- Use `nullable` reference types
- Async methods should accept `CancellationToken`
- Use source-generated `[LoggerMessage]` for logging (CA1848 compliant)

### Testing

- Write tests for all new functionality
- Use xUnit, NSubstitute for mocks, EF Core InMemory for database
- Run `dotnet test` before submitting a PR — all tests must pass
- Aim for both unit tests and integration tests

### Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

- `feat:` — new feature
- `fix:` — bug fix
- `test:` — adding or updating tests
- `docs:` — documentation changes
- `chore:` — maintenance tasks
- `refactor:` — code restructuring without behavior change

## Pull Requests

1. Ensure all tests pass (`dotnet test`)
2. Ensure the build has 0 warnings (`dotnet build`)
3. Update the README if your change affects the public API
4. Describe what your PR does and why

## Reporting Issues

- Use GitHub Issues
- Include .NET version, CashierMollie version, and a minimal reproduction if possible
- For security vulnerabilities, please email security@theolymp.net instead of opening a public issue

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
