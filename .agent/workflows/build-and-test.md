---
description: Restore, build in Release mode, and run all YesVPN unit and integration tests
---
1. Run `dotnet restore` in the repo root.
2. Run `dotnet build -c Release --no-restore`.
3. Run `dotnet test -c Release --no-build --verbosity normal`.
4. Report any build warnings/errors or failing tests with file paths and line numbers.
