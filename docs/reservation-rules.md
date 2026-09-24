# Reservation Rules

These are the rules planned for LU Thrift. The room's hours and physical pickup process still need confirmation.

## Making a reservation

- A reservation lasts one hour from the time the site accepts it.
- A student can have up to three active reservations. Cancelled, expired, and picked-up reservations don't count.
- Reservations can only start during pickup hours, and the whole hour must fit before closing or a break. Ending exactly at closing is allowed.
- An item can have only one active reservation. If several eligible students try to reserve it at the same time, exactly one succeeds. The others get a message that it's no longer available.
- The limits must still hold when someone submits multiple requests or uses several tabs.

For example, if pickup closes at 5 p.m., a reservation can start at 4 p.m., but not 4:01. This is just an example, not the room's confirmed schedule.

## Expiration and cancellation

At the one-hour deadline, the student no longer has a claim to the item. The deadline still applies if a page or background job hasn't refreshed yet.

Students can cancel their own active reservations at any time. After cancellation or expiration, the item becomes available again if it's still physically present and hasn't been marked Missing or Archived. The reservation also stops counting toward the student's limit.

**Proposed:** keep ended reservations visible with their status so students can see what happened.

## Pickup

Staff confirm the physical handoff to the student with the active reservation. Students can't confirm pickups themselves. The site rechecks the reservation before recording the pickup.

A completed pickup ends the reservation and leaves the item unavailable. An expired or cancelled reservation can't be used for pickup. If the item is still available, the student can make a new reservation under the same rules.

## Missing items

If staff can't find an item, they mark it Missing. This removes it from availability and cancels any active reservation with a reason the student can see.

It stays unavailable until staff locate it. Expiration or cancellation must never make a Missing item available again. **Proposed:** staff check a located item before returning it to the catalog; the cancelled reservation isn't automatically restored.

## Physical holds — To Be Confirmed

The website can't guarantee a physical hold unless staff separate and protect reserved items. We still need to confirm staffing, where reserved items would be kept, how they're released, and how walk-in pickups would work.

Exact pickup hours, schedule changes, and the way staff verify students also need confirmation. See the [open questions](requirements.md#to-be-confirmed).
