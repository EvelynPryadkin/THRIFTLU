# Development Log

## Week 1: Planning and project setup

I set up the THRIFTLU repository and organized the folders for the website, tests, and documentation. Most of the files were placeholders at this point.

I wrote the project overview, requirements, and reservation rules. The main goal is to let students see what is available in the thrift room before visiting and reserve an item for pickup. I outlined what students, staff, and administrators would need to do, along with the proposed one-hour reservation period and limit of three active reservations per student.

I chose ASP.NET Core Razor Pages with C#, EF Core, PostgreSQL, and xUnit. Pickup hours and the process for holding items still needed to be discussed with thrift-room staff.

## Week 2: Connecting the database

I set up the .NET 10 web and test projects and added them to the solution. I then connected the app to PostgreSQL through EF Core, with the database running in Docker. I used port 5433 because another project was already using 5432. The database data is stored in a Docker volume, the password is in an ignored `.env` file, and the connection string is in user secrets.

I added the user, category, listing, and reservation models and applied the first migration, `InitialDatabase`. I added three categories and four sample items to test the connection. The home page now loads those items and their categories from PostgreSQL.

I also added a test to check that reservations must belong to a user and a listing, and that deleting either cannot automatically erase reservation history.

The build finished without warnings or errors, and the test passed. I checked that all four sample items appeared on the home page and that EF Core reported no pending model changes. The whitespace check passed too. The local HTTPS certificate still needed to be trusted, so the page check bypassed certificate validation.

By the end of Week 2, the database and sample inventory display were working. Sign-in, reservation actions, and staff forms were still to come.

## Week 3: Accounts and page access

I added registration, login, and logout using ASP.NET Core Identity and the existing PostgreSQL tables. Registration asks for a display name, email, password, and password confirmation. The display name is required and limited to 100 characters. The server sets the account's creation time and active status, and Identity handles email normalization and password hashing.

I added Student, Staff, and Administrator roles. The app creates missing roles at startup and checks for existing ones so restarting it does not add duplicates. New accounts receive Student automatically. Creating the account and assigning that role happen in one transaction, so a failed assignment does not leave a partly registered account or sign the user in.

For local development, an email in user secrets can give Administrator access to an account that already exists. This only runs in Development and does not create an account or set a password. After the role changes, the user needs to log out and back in.

I protected the Staff folder for staff and administrators, and the Admin folder for administrators only. Anonymous visitors are sent to Login. Signed-in users without permission see Access Denied. The navigation shows the links each role can use, but entering a protected URL directly still goes through the server's access checks. I added simple Staff and Admin landing pages to check this without building their tools yet.

I added tests that run the app with SQLite in memory, so checking page access does not need Docker or touch the development database. They cover the role access matrix, repeated role setup, bootstrap restrictions, and registration failures. I also added HTTP form tests for attempts to submit a higher role, missing antiforgery tokens, generic login errors, and logout. The original database-model test is still included.

The final restore and build passed with no warnings or errors. All 47 tests passed, and EF Core reported no pending model changes. These checks did not connect to the development database.

The home page remains the working public catalog. The other catalog and private account files are still placeholders, so their tests check the folder rules rather than working pages. Reservation actions, ownership checks, inventory tools, and user administration are still for later weeks. Email confirmation remains disabled because no email service is configured.
