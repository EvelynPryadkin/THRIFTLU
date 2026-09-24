# LU Thrift

LU Thrift is a website planned for the Lawrence University thrift room in Colman Hall. Students currently have to visit and search the room to see what's available. The goal is to let them browse online and reserve free items before heading over.

## First version

- Students can browse, search, filter, view item details, and manage their own reservations.
- Staff can add, edit, publish, archive, or mark items missing, and confirm pickups.
- Administrators can manage staff access and operating settings.

Reservations last one hour, with a limit of three active reservations per student. The full hour has to fit within pickup hours. See the [reservation rules](docs/reservation-rules.md) for the details.

The first release covers official thrift-room inventory only. Student Exchange might come later. There are no payments, auctions, ratings, or real-time chat.

## Proposed stack

ASP.NET Core Razor Pages and C#, with Entity Framework Core, PostgreSQL, and xUnit.

## Status

Week 1 planning is complete. Implementation hasn't started; the source and test folders are still placeholders. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

## Documentation

- [Requirements](docs/requirements.md)
- [Reservation rules](docs/reservation-rules.md)

Repository name: THRIFTLU.
