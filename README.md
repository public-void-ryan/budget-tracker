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
dotnet test
docker build -t budget-tracker . && docker run -p 8080:8080 budget-tracker
```

## Roadmap

1. Skeleton and CI ← current
2. Data model and migrations
3. Core CRUD API
4. Auth
5. CSV import with dedup
6. Categorization rules and budgets
7. UI and E2E tests
8. Deploy and write-up
