# Development Log

## Week 2 — Database foundation

Checked that the .NET 10 web and test projects are connected through the solution and that the test project references the web project.

Set up PostgreSQL with Docker Compose and connected the app through EF Core. The database uses port 5433 because another project uses 5432. Its data stays in a named volume. The password is in the ignored .env file, and the connection string is in user secrets.

Added the user, category, listing, and reservation models, then applied InitialDatabase using the local dotnet-ef tool. Seeded three categories and four sample items with fixed IDs and timestamps. No users, roles, or reservations were seeded.

The home page reads the sample items and their categories from the database. Replaced the empty generated test with a check that both reservation relationships are required and use restrictive delete rules.

Checks completed:

- Restore and build passed with no build warnings or errors; 1 test passed.
- PostgreSQL was healthy, and the migration was up to date with no pending model changes.
- The home page returned HTTP 200 and displayed all four items. The app was stopped after checking it.
- Git's whitespace check passed.

The local HTTPS certificate still needs to be trusted. The page check accepted that development certificate.

Authentication, roles, reservation actions, staff forms, and production setup are still later work.
