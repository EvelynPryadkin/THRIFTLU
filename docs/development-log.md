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
