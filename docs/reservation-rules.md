# Reservation Rules

These rules describe the reservation policy for the LU Thrift Room. Official pickup hours and operating procedures still need confirmation from thrift-room staff. Week 6 checkpoint 1 implements policy and pickup-hours eligibility only; reservation creation, cancellation, expiration processing, and pickup confirmation are not implemented yet.

## Reservation Duration

* A reservation lasts for **one hour**.
* The one-hour period begins when the system successfully confirms the reservation.
* The student will be shown the exact time by which the item must be collected.
* The reservation is no longer valid once the deadline has been reached.
* One hour means 60 minutes of elapsed time, even when daylight saving time changes the local clock. The server supplies the start and deadline; the browser cannot choose them.

## Reservation Limit

* A student may have up to **three active reservations** at one time.
* Completed, canceled, and expired reservations do not count toward this limit.
* If a student already has three active reservations, the system will not allow another reservation until one is completed, canceled, or expired.
* A reservation counts as active only when `Status == Active` **and** `ExpiresAtUtc > current server time`. At `ExpiresAtUtc == current server time`, it is expired and does not count, even if a later cleanup process has not yet updated its stored status.
* The one-hour duration and maximum of three are fixed in `ReservationPolicy`; they are not schedule configuration settings or form fields.

## Pickup Hours

* Students may only reserve items during the thrift room’s established pickup hours.
* A reservation will only be accepted if the complete one-hour reservation period fits within one continuous open pickup interval.
* Opening time is included. Starting exactly one hour before closing is allowed; starting even one tick later is not. The deadline may equal closing time, but the reservation is no longer active at that deadline.
* A reservation cannot start during a break or span a break. Separate weekly intervals on the same day describe the open periods on either side of a break.
* A closed date overrides the weekly schedule. Weekdays and closed dates are interpreted in the campus timezone, not the server machine's timezone or the student's device timezone.
* When hours are missing or invalid, new reservations remain unavailable. Public browsing continues to work.

## Configuration and Time Handling

The `Reservations` configuration section ships without pickup hours because none have been confirmed:

```json
{
  "Reservations": {
    "CampusTimeZone": "America/Chicago",
    "WeeklyPickupIntervals": [],
    "ClosedDates": []
  }
}
```

Once staff confirm the schedule, each `WeeklyPickupIntervals` entry needs:

| Field | Meaning |
| --- | --- |
| `Day` | An English weekday name, such as `Monday`; case does not matter. |
| `OpensAt` | Campus-local opening time in 24-hour `HH:mm` format. |
| `ClosesAt` | Campus-local closing time in 24-hour `HH:mm` format, later on the same day. |

`ClosedDates` contains campus-local dates in `yyyy-MM-dd` format. There is no schedule-management screen. Configure the confirmed values through the normal ASP.NET Core configuration sources and restart the application after changes. The tests use synthetic hours; those are not an official schedule.

Intervals must stay within one calendar day. Overlapping, duplicate, or touching intervals are invalid; write a continuous open span as one interval instead. Invalid weekdays, times, dates, or timezone IDs make the schedule unavailable rather than guessing a correction. Blank or incomplete intervals are invalid too. Overnight hours and date-specific replacement hours are outside this checkpoint.

`PickupHoursEligibility` reads one instant from injected `TimeProvider`, converts it to the configured campus timezone to select the date and weekday, and converts that day's open-interval endpoints back to UTC. It adds one elapsed hour to the captured UTC instant and checks that both start and deadline fit the same interval. Eligible results contain UTC start and expiration values; ineligible results contain a reason and no reservation window. This is a read-only eligibility calculation, not a booking or a promise that an item remains available. A future reservation operation must recheck eligibility, the student's active count, and the listing together when accepting the reservation.

If a configured endpoint falls in a daylight-saving gap or repeated hour, that day's schedule is unavailable: the helper does not guess which instant staff intended. When endpoints are unambiguous, an interval may include a clock change; duration is still measured in UTC. Normal winter and summer dates therefore use the campus timezone's applicable offset rather than a hard-coded offset. All persisted reservation timestamps remain UTC in the existing database columns.

## Cancellation, Expiration, and Release

These are the rules for later reservation operations, not features completed in this checkpoint:

* A student may cancel their own active reservation before its deadline. Cancellation changes its status to `Canceled`, removes it from the active count, and releases its hold. Students cannot cancel another student's reservation.
* At or after the deadline, treat an `Active` reservation as expired, not as an on-time cancellation or pickup. Expiration processing will persist `Expired` and release the hold. A delayed worker must not extend the deadline or make the reservation count as active.
* Releasing a hold returns its listing from `Held` to `Available` only if that reservation still owns the hold. It must not overwrite a different hold or turn a Collected, Missing, or Archived listing back into Available.
* Updating the reservation and releasing its hold must be atomic. Keep reservation and inventory history; do not delete records to cancel or expire them. Reservation creation and release will need concurrency protection in a later checkpoint.
* Staff pickup confirmation remains future work. It will verify the reservation and change it to `Fulfilled` while recording the collection; students must not be able to confirm their own pickup. The detailed staff procedure and any late-arrival exceptions still need agreement.

## Decisions Still Needed

* Confirm official weekly pickup days, opening and closing times, and breaks.
* Confirm closure dates, holidays, and who maintains configuration changes.
* Confirm `America/Chicago` as the campus timezone and the procedure for any hours near a daylight-saving change.
* Decide whether overnight openings or one-off replacement hours will ever be needed.
* Agree on pickup identity checks, staff confirmation steps, and late-arrival handling.
* Decide when expiration cleanup will run and how staff will handle an item that cannot be found. The exact-deadline policy above must hold regardless of cleanup timing.
