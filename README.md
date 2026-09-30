# LU Thrift

LU Thrift is a senior capstone project for the Lawrence University thrift room in Colman Hall. Students currently have to visit and search the room to see what's available. The goal is to let them browse online and reserve free items before heading over.

## Planned first version

- Students can browse, search, filter, view item details, and manage their own reservations.
- Staff can add, edit, publish, archive, or mark items missing, and confirm pickups.
- Administrators can manage staff access and operating settings.

Reservations last one hour, with a limit of three active reservations per student. The full hour has to fit within pickup hours. See the [reservation rules](docs/reservation-rules.md) for the details.

The first release covers official thrift-room inventory only. Student Exchange might come later. There are no payments, auctions, ratings, or real-time chat.

## Stack

ASP.NET Core Razor Pages and C# on .NET 10, with Entity Framework Core, PostgreSQL, and xUnit.

## Status

The Week 2 database foundation is in place. The home page reads sample items from a local PostgreSQL database, and a smoke test checks that deleting users or listings can't erase reservation history.

Registration, login, and logout are available. New accounts receive the Student role. Student, Staff, and Administrator roles are created at startup; reservation actions and staff tools are still planned. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

## Running locally

You'll need the .NET 10 SDK and Docker. On a fresh checkout, copy .env.example to .env and choose a local database password. Set the same password in the web project's user secrets, replacing YOUR_LOCAL_PASSWORD below:

```sh
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=127.0.0.1;Port=5433;Database=luthrift;Username=luthrift;Password=YOUR_LOCAL_PASSWORD" \
  --project src/LUThrift.Web
```

Then start the database, apply the migration, and run the app:

```sh
docker compose up -d --wait
dotnet tool restore
dotnet ef database update --project src/LUThrift.Web -- --environment Development
dotnet run --project src/LUThrift.Web --launch-profile https
```

Open https://localhost:7131. The local development certificate may need to be trusted in your browser. Run the test with `dotnet test LUThrift.slnx`.

### Optional development administrator

Register a local account first. To give that existing account the Administrator role during development, store its email in user secrets, replacing the placeholder:

```sh
dotnet user-secrets set "DevelopmentAdmin:Email" "YOUR_EXISTING_ACCOUNT_EMAIL" \
  --project src/LUThrift.Web
```

Restart the app with the Development environment, then log out and back in to refresh the account's role claims. The bootstrap never creates an account or sets a password. If the account does not exist, it logs a warning without the email. It does nothing outside Development, even if the setting is present.

After the role has been assigned, remove the optional setting:

```sh
dotnet user-secrets remove "DevelopmentAdmin:Email" --project src/LUThrift.Web
```

Removing the setting does not remove an already assigned role. Keep actual emails and credentials out of source files. Ordinary registration always assigns Student on the server; there is no role selector or self-service role management. The initializer checks existing roles and membership on every startup, so repeated starts do not duplicate them. If required role creation or administrator assignment fails, startup fails rather than serving the app with incomplete initialization.

### Page access

Authorization is enforced by Razor Pages folder conventions in `Program.cs`:

| Page or folder | Access |
| --- | --- |
| `/` and `/Thrift` | Public |
| `/Account` | Signed-in users |
| `/Staff` | Staff or Administrator |
| `/Admin` | Administrator |

These rules cover pages in nested folders too. Anonymous visitors to protected pages are redirected to Login. Signed-in users without the required role are redirected to Access Denied, which returns HTTP 403. Staff and Admin navigation links follow the user's roles, but the server checks access even when someone enters a URL directly.

The home page is the current public catalog. Staff and Admin have minimal landing pages; the other catalog, account, inventory, pickup, and administration files are still empty placeholders without routes. The folder rules will apply as those pages are implemented. Reservation ownership checks remain future work.

## Documentation

- [Requirements](docs/requirements.md)
- [Reservation rules](docs/reservation-rules.md)
- [Development log](docs/development-log.md)

Repository name: THRIFTLU.
