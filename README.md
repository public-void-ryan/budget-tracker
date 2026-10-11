# Budget Tracker

A personal-finance API: import bank transactions, categorize them automatically, and track spending against monthly budgets.

**Status:** milestone 1 of 8 (skeleton, tests, CI, Docker).

## Planned scope

- Accounts, transactions, categories, monthly budgets
- CSV import with duplicate detection
- Rule-based auto-categorization
- Budget vs actual summary per month
- Token auth, per-user data isolation
- Minimal web UI with Playwright end-to-end tests

Deliberately out of scope: bank-API integrations, multi-currency, mobile app.

## Stack

C# / .NET 10 minimal API, PostgreSQL (EF Core), xUnit, Docker, GitHub Actions.

## Develop

```bash
dotnet run --project src/BudgetTracker.Api   # http://localhost:5268/health
dotnet test                                  # needs Docker (Testcontainers starts PostgreSQL)
docker build -t budget-tracker . && docker run -p 8080:8080 budget-tracker
```

### Local database

```bash
docker compose up -d --wait        # PostgreSQL 17 on localhost:5432
dotnet tool restore                # installs the pinned dotnet-ef
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update --project src/BudgetTracker.Api
```

The connection string for local development is in `appsettings.Development.json` and
matches `docker-compose.yml`. Both are for a throwaway local container only.

### Migrations in CI

CI builds an EF migration bundle (`dotnet ef migrations bundle`) and applies it to an empty
PostgreSQL service container, then applies it again to prove a second run is a no-op. The
bundle is uploaded as a build artifact. The application never migrates its own schema on
startup: deploys will run the bundle first, so the app's database user needs only data rights.

## Roadmap

1. Skeleton and CI ← current
2. Data model and migrations
3. Core CRUD API
4. Auth
5. CSV import with dedup
6. Categorization rules and budgets
7. UI and E2E tests
8. Deploy and write-up
