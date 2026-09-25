# Requirements

## Problem and goal

Students have to visit the thrift room in Colman Hall and look through it to find out what's available. LU Thrift will let them check official inventory online and reserve free items for pickup. Staff will use the site to keep the catalog up to date.

## Users and first-release features

- **Students:** browse, search, filter, and view items. Reserve available items, see their own reservations, and cancel ones they no longer need.
- **Staff:** create, edit, publish, and archive inventory, mark items missing, and confirm physical pickups.
- **Administrators:** manage staff access and operating settings.

Students can't manage inventory, confirm pickups, or view or change someone else's reservations. Staff access alone doesn't allow changes to administrator settings.

**Proposed:** each listing has a title, description, category, condition, and photo with a text description. Search covers titles and descriptions, with category and condition filters.

## Required behavior

Only published, available items can be reserved. Unpublished, archived, missing, and picked-up items must not be offered as available.

Reservations last one hour. A student can have at most three active reservations, and an item can have only one. Reservations can start only during pickup hours, when the full hour fits before closing. If eligible students try to reserve the same available item at once, exactly one succeeds.

Students can see their reservation status and deadline. Cancellation or expiration ends their claim. If the item is still in the room and hasn't been marked missing or archived, it becomes available again. Staff confirm the actual pickup, which ends the reservation and removes the item from availability.

Marking an item Missing removes it from availability and cancels any active reservation with a reason the student can see. It stays unavailable until staff locate it.

The server must check permissions, timing, limits, and availability when an action is submitted. These checks still apply if someone uses multiple tabs or loads an outdated page. The [reservation rules](reservation-rules.md) cover the details.

## Quality goals

- **Security:** check roles and reservation ownership, validate input, and keep student information private. Don't store credentials in the repository.
- **Accessibility:** support keyboard use, clear labels, readable contrast, image descriptions, and errors that don't rely only on color.
- **Reliability:** avoid duplicate reservations and inconsistent item statuses. Use server time for deadlines and show confirmation only after saving.
- **Mobile use:** make browsing, reservations, and staff pickup screens usable on a phone.

These are goals for implementation. The stack is listed in the [README](../README.md#stack).

## Out of scope

Student Exchange is a possible future feature. The first release won't include student-created listings, payments, auctions, ratings, real-time chat, shipping, or delivery.

## To Be Confirmed

- What are the pickup hours, local timezone, breaks, and holiday closures? What happens to existing reservations if the room closes unexpectedly?
- Will staff be there during pickup hours? How will they set reserved items aside, protect them, and release them after cancellation or expiration?
- Can students still take items as walk-ins, and how will those pickups be recorded?
- Who will list items, check inventory, verify students at pickup, and train or cover for other staff?
- Who handles missing-item reports, tells affected students, and checks items before returning them to availability?
- How will campus sign-in and student eligibility work? Will browsing require sign-in?
- Who grants and removes Staff and Administrator access, including setting up the first administrator?
- Where will the site run, and what help will be needed from Lawrence IT?
- Who owns the database and image storage? Who decides data access and retention, and handles backups and recovery?
- Who maintains the application after the capstone, and what needs to be handed over?

The website can record a reservation, but it can't guarantee a physical hold unless staff separate and protect the item.
