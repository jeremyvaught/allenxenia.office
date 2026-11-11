using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using static AllenBuilding.DemoSeed.When;

namespace AllenBuilding.DemoSeed;

// The fixed demonstration content. Every person and business is fictional; only the building name is real.
// Times count business days back from the run, so the same content always looks two months old.
internal static class DemoContent
{
    // A shared password for throwaway local accounts, like the browser fixture's.
    public const string Password = "Allen-demo-2026!";
    public const string Building = "Allen Building";
    public static readonly string[] Suites = ["101", "102", "201", "202", "203", "301", "302"];

    public static readonly Person Jordan = new("jordan", "Jordan Hale", "jordan.hale@example.test");
    public static readonly Person Casey = new("casey", "Casey Brandt", "casey.brandt@example.test");
    public static readonly When Opened = Day(42, 8, 30);

    public static readonly Person Dana = new("dana", "Dana Whitcomb", "dana.whitcomb@example.test");
    public static readonly Person Marcus = new("marcus", "Marcus Tolliver", "marcus.tolliver@example.test");
    public static readonly Person Priya = new("priya", "Priya Raman", "priya.raman@example.test");
    public static readonly Person Elena = new("elena", "Elena Okafor", "elena.okafor@example.test");
    public static readonly Person Thomas = new("thomas", "Thomas Kestrel", "thomas.kestrel@example.test");
    public static readonly Person Renee = new("renee", "Renee Albright", "renee.albright@example.test");
    public static readonly Person Sam = new("sam", "Sam Delgado", "sam.delgado@example.test");
    // Suite 203's earlier tenant. Their access is revoked before Kestrel Law Office moves in.
    public static readonly Person Lou = new("lou", "Lou Harlan", "lou.harlan@example.test");

    public static readonly TenantSeed[] Tenants =
    [
        new(Dana, "Birchline Accounting", "101", Day(41, 9, 5), Day(41, 13, 10)),
        new(Marcus, "Tolliver Insurance Agency", "102", Day(41, 9, 8), Day(41, 9, 50)),
        new(Priya, "Second Street Design Studio", "201", Day(41, 9, 11), Day(40, 8, 40)),
        new(Elena, "Meridian Physical Therapy", "202", Day(41, 9, 14), Day(41, 16, 25)),
        new(Lou, "Harlan & Pike Surveying", "203", Day(41, 9, 17), Day(40, 10, 15)),
        new(Renee, "Brightwater Counseling", "301", Day(41, 9, 20), Day(39, 11, 30)),
        new(Sam, "Northgate Title", "302", Day(41, 9, 23), Day(41, 10, 45)),
        new(Thomas, "Kestrel Law Office", "203", Day(19, 9, 15), Day(19, 11, 40)),
    ];
    public static readonly TenantSeed FormerTenant = Tenants[4];
    public static readonly When FormerTenantRevoked = Day(20, 16, 0);
    public const string FormerTenantReason = "Lease ended and the suite was turned over to a new tenant.";

    // Second contacts who have not accepted: the first invitation has expired, the second is still open.
    public static readonly InviteSeed ExpiredInvitation =
        new(new("owen", "Owen Fitch", "owen.fitch@example.test"), "Birchline Accounting", "101", Day(12, 10, 0));
    public static readonly InviteSeed PendingInvitation =
        new(new("nia", "Nia Brooks", "nia.brooks@example.test"), "Meridian Physical Therapy", "202", Day(2, 15, 30));

    public const string Greene = "Greene Valley Plumbing";
    public const string Ironwood = "Ironwood Electric";
    public const string Fairground = "Fairground Heating & Cooling";
    public const string Buckeye = "Buckeye Lock & Door";
    public static readonly ContractorSeed[] Contractors =
    [
        new(Greene, Trade.Plumbing, "dispatch@greenevalleyplumbing.example.test", "937-555-0142", Day(42, 9, 30)),
        new(Ironwood, Trade.Electrical, "service@ironwoodelectric.example.test", "937-555-0117", Day(42, 9, 33)),
        new(Fairground, Trade.HeatingCooling, "office@fairgroundhvac.example.test", "937-555-0168", Day(42, 9, 36)),
        new(Buckeye, Trade.General, "jobs@buckeyelockdoor.example.test", "937-555-0193", Day(42, 9, 39), Deactivated: Day(25, 14, 0)),
    ];

    public static readonly ArticleSeed[] Articles =
    [
        new("After-hours access", ArticleAudience.ActiveTenant, Jordan, Day(42, 10, 20), Day(42, 10, 35), """
            The front entrance is unlocked from 7:00 a.m. to 6:00 p.m., Monday through Friday. Outside those hours, use your suite's key fob at the reader beside the front door. The back entrance is fob-only at all times.

            The elevator runs at all hours. Hallway lights on each floor come on when you step off the elevator or out of the stairwell and turn off after 20 minutes without movement.

            If you are the last person out, check that the front door has latched behind you. Please do not prop either entrance open after hours.

            Lost a fob? Send a maintenance request from the portal and a manager will turn the old fob off and issue a new one.
            """),
        new("Reporting an emergency", ArticleAudience.Public, Jordan, Day(42, 10, 45), Day(42, 11, 0), """
            Call 911 first for fire, smoke, a gas smell, or anyone who is hurt or in danger. Then tell building management.

            For a building emergency that is not life-threatening, such as water coming through a ceiling, no power on a whole floor, a stuck elevator, or a door that will not lock, call the building emergency line at 937-555-0100. It is answered at all hours.

            If water is leaking, turn off the valve under the sink or behind the toilet if you can reach it safely, and move equipment and paper out of the way.

            Everything else belongs in a maintenance request from the portal. Requests are read each business morning and throughout the day.
            """),
        new("Heating and cooling schedule", ArticleAudience.ActiveTenant, Casey, Day(41, 14, 10), Day(41, 14, 30), """
            Each suite has its own thermostat. The building's equipment runs on this schedule:

            Weekdays, 6:30 a.m. to 6:30 p.m.: occupied. Thermostats can be set between 68 and 74 degrees.
            Evenings and weekends: setback. Heating holds 62 degrees and cooling holds 80 degrees.

            Need the system on outside those hours for a late meeting or a weekend deadline? Send a maintenance request at least one business day ahead with the date and times.

            The building switches from cooling to heating in the fall and back in the spring. For about a week on either side of the change, a suite can run a few degrees warm or cool. If your suite is more than four degrees off its setting for over an hour, send a request and mention the reading on the thermostat.
            """),
        new("Parking and deliveries", ArticleAudience.Public, Casey, Day(41, 15, 0), Day(40, 9, 10), """
            Visitor parking is in the front lot. Spaces are not assigned except the two marked for each first-floor suite. The rear lot is for tenants and their staff.

            Accessible spaces are next to the front entrance, which has a push-button door opener.

            Deliveries come to the back entrance. Carriers can ring the suite from the panel beside the door. The loading zone is limited to 20 minutes. For freight on a pallet or anything too large for the elevator, tell building management a day ahead so the stairwell doors can be opened.

            Packages are not accepted on a tenant's behalf. Please arrange for someone in your suite to receive them.
            """),
        new("Trash and recycling", ArticleAudience.ActiveTenant, Casey, Day(40, 9, 30), Day(40, 9, 45), """
            Suite trash and recycling are collected on Monday, Wednesday and Friday evenings. Leave bins inside your suite door; the cleaning crew has access.

            Recycling takes paper, flattened cardboard, cans and plastic bottles. Please keep food and liquids out of the blue bins, since one contaminated bin sends the whole load to landfill.

            Break down boxes and stack them flat beside your recycling bin. For more than ten boxes at once, take them to the cardboard dumpster in the rear lot.

            Electronics, toner cartridges, batteries and furniture do not go in the dumpsters. Send a maintenance request and we will arrange a pickup.
            """),
        new("Moving in or out", ArticleAudience.Public, Jordan, Day(40, 13, 20), Day(39, 8, 50), """
            Schedule your move with building management at least five business days ahead. Moves take place on weekdays after 5:00 p.m. or on Saturdays, so the elevator and hallways stay clear for other tenants.

            Before moving day we pad the elevator and put floor protection down in the lobby and the hallway to your suite. Movers must use the back entrance.

            Moving in: you will receive key fobs and suite keys at your walkthrough. Each person who needs the tenant portal gets an email invitation from building management.

            Moving out: return all keys and fobs at the final walkthrough. Leave the suite empty and broom-clean. Portal access for the suite ends on the move-out date.
            """),
        new("Holiday hours and snow removal", ArticleAudience.ActiveTenant, Jordan, Day(6, 15, 10), null, """
            DRAFT. Confirm the plow contractor's trigger depth and the holiday list before publishing.

            The front entrance stays locked on building holidays. Key fobs work as usual.

            Snow: the lots and walks are cleared once two inches have fallen, starting with the front entrance and accessible spaces.
            """),
    ];

    public static IReadOnlyList<RequestScript> Requests() =>
    [
        // Closed.
        new RequestScript(Lou, "Front door lock sticks",
                "The deadbolt on our suite door takes three or four tries to turn. Yesterday I couldn't lock up until I leaned on the door.",
                "Suite 203 entry door", Day(38, 9, 12))
            .Triage(Casey, Day(38, 10, 5), Priority.Normal, Trade.General)
            .Assign(Casey, Day(38, 10, 9), Buckeye)
            .Start(Casey, Day(37, 13, 30))
            .Complete(Casey, Day(36, 11, 15), "Buckeye Lock & Door replaced the worn deadbolt cylinder and adjusted the strike plate. Two new keys left with the tenant.")
            .Close(Jordan, Day(34, 9, 40)),
        new RequestScript(Lou, "Light out over the reception desk",
                "One of the recessed lights over our front desk went dark this morning. The other three are fine.",
                "Suite 203 reception", Day(30, 8, 48))
            .Triage(Jordan, Day(30, 9, 20), Priority.Low, Trade.Electrical)
            .AssignSelf(Jordan, Day(30, 9, 21))
            .Start(Jordan, Day(29, 14, 5))
            .Complete(Jordan, Day(29, 14, 40), "Replaced the failed LED driver and lamp. All four fixtures are working.")
            .Close(Casey, Day(27, 10, 10)),
        new RequestScript(Dana, "Leaking sink in the kitchenette",
                "There's a steady drip under the kitchenette sink. We put a bucket under it but the cabinet floor is already wet.",
                "Suite 101 kitchenette", Day(37, 8, 25))
            .Triage(Jordan, Day(37, 9, 2), Priority.High, Trade.Plumbing)
            .Assign(Jordan, Day(37, 9, 6), Greene)
            .Reply(Jordan, Day(37, 9, 10), "Greene Valley Plumbing will be there between 1 and 3 this afternoon. Please keep the bucket in place until then.")
            .Says(Day(37, 9, 31), "Thanks. Someone will be here all afternoon.")
            .Start(Jordan, Day(37, 13, 20))
            .Complete(Jordan, Day(37, 14, 45), "Replaced the P-trap and both supply lines under the sink. Cabinet floor dried and checked; no damage to the base.")
            .Says(Day(36, 8, 50), "Dry this morning. Thank you for the quick turnaround.")
            .Close(Jordan, Day(35, 9, 15)),
        new RequestScript(Marcus, "Thermostat stuck at 64",
                "Our thermostat reads 64 and won't go higher no matter what we set. Clients are keeping their coats on.",
                "Suite 102, wall by the front desk", Day(36, 8, 5))
            .Triage(Jordan, Day(36, 8, 40), Priority.High, Trade.HeatingCooling)
            .Assign(Jordan, Day(36, 8, 44), Fairground)
            .Says(Day(36, 10, 15), "Any idea when someone is coming? It's 62 in here now.")
            .Reply(Jordan, Day(36, 10, 32), "Fairground Heating & Cooling is on the way and should arrive by 11:30. I've left a space heater at your front desk in the meantime.")
            .Start(Jordan, Day(36, 11, 25))
            .Complete(Jordan, Day(36, 15, 10), "Fairground replaced the zone valve actuator for suite 102 and recalibrated the thermostat. Holding at 70.")
            .Close(Casey, Day(34, 9, 5)),
        new RequestScript(Elena, "Hallway light flickering outside suite 202",
                "The ceiling light in the hall right outside our door flickers constantly. A couple of patients have said it bothers them.",
                "Second-floor hallway, outside suite 202", Day(33, 11, 40))
            .Triage(Casey, Day(33, 13, 5), Priority.Normal, Trade.Electrical)
            .Assign(Casey, Day(33, 13, 8), Ironwood)
            .Start(Casey, Day(32, 9, 0))
            .Complete(Casey, Day(32, 9, 50), "Ironwood Electric replaced the ballast and both tubes in the hallway fixture.")
            .Close(Casey, Day(30, 15, 20)),
        new RequestScript(Renee, "Restroom fan rattles",
                "The exhaust fan in the third-floor restroom rattles loudly whenever the light is on. You can hear it from our waiting room.",
                "Third-floor restroom", Day(28, 9, 35))
            .Triage(Jordan, Day(28, 10, 10), Priority.Low, Trade.General)
            .AssignSelf(Jordan, Day(28, 10, 11))
            .Note(Jordan, Day(28, 10, 15), "Same fan model as the second-floor restroom. If the motor is going, order two.")
            .Start(Jordan, Day(26, 8, 30))
            .Complete(Jordan, Day(26, 9, 20), "Tightened the fan housing and replaced a cracked blade. Running quietly.")
            .Close(Casey, Day(24, 11, 0)),
        new RequestScript(Sam, "Dead outlet by the front window",
                "The outlet under the front window stopped working. Nothing tripped in our panel that I can see.",
                "Suite 302, front window wall", Day(26, 13, 15))
            .Triage(Casey, Day(26, 14, 0), Priority.Normal, Trade.Electrical)
            .Assign(Casey, Day(26, 14, 4), Ironwood)
            .Start(Casey, Day(25, 10, 30))
            .Complete(Casey, Day(25, 11, 20), "Ironwood Electric found a failed GFCI outlet upstream on the same circuit and replaced it. Both outlets tested.")
            .Close(Jordan, Day(23, 9, 30)),
        new RequestScript(Priya, "Ceiling stain after rain",
                "A brown stain about the size of a dinner plate showed up on the ceiling after this week's storm. It's above our sample shelves, so we've moved everything out from under it.",
                "Suite 201, north wall above the sample shelves", Day(24, 8, 20))
            .Triage(Jordan, Day(24, 8, 55), Priority.High, Trade.General)
            .AssignSelf(Jordan, Day(24, 8, 56))
            .Note(Jordan, Day(24, 9, 5), "The restroom supply line for suite 301 runs above this spot. Rule that out before calling the roofer.")
            .Start(Jordan, Day(24, 10, 30))
            .Says(Day(23, 9, 10), "The stain hasn't grown, but the tile feels damp to the touch.")
            .Reply(Jordan, Day(23, 9, 45), "Thanks, that helps. We traced it to a leaking pipe joint above your ceiling, not the roof. It's being repaired today and the tile will be replaced once it dries out.")
            .Complete(Jordan, Day(21, 15, 30), "Re-soldered the leaking joint on the supply line above suite 201 and replaced two ceiling tiles. No further moisture after 48 hours.")
            .Close(Casey, Day(19, 10, 0)),
        new RequestScript(Marcus, "Elevator call button on the first floor doesn't light",
                "The up button at the first-floor elevator doesn't light when you press it. The elevator still comes, but people keep pressing it over and over.",
                "First-floor elevator lobby", Day(15, 9, 50))
            .Triage(Casey, Day(15, 10, 30), Priority.Normal, Trade.Electrical)
            .Assign(Casey, Day(15, 10, 33), Ironwood)
            .Start(Casey, Day(13, 8, 45))
            .Complete(Casey, Day(13, 9, 30), "Ironwood Electric replaced the burned-out lamp module behind the call button.")
            .Close(Casey, Day(11, 14, 15)),

        // Cancelled.
        new RequestScript(Renee, "Squeaky hinge on the office door",
                "The hinge on our inner office door squeaks badly enough to interrupt sessions.",
                "Suite 301, inner office door", Day(22, 10, 5))
            .Triage(Casey, Day(22, 11, 0), Priority.Low, Trade.General)
            .Says(Day(21, 8, 40), "One of us brought some oil in this morning and it's quiet now. You can skip this one.")
            .Reply(Casey, Day(21, 9, 5), "Glad it's sorted. I'll close this out.")
            .Cancel(Casey, Day(21, 9, 6), "Tenant fixed the hinge themselves. No work needed."),
        new RequestScript(Dana, "Parking lot light out near the back entrance",
                "The pole light closest to the back door has been out all week. It's very dark when we leave after five.",
                "Rear parking lot, by the back entrance", Day(12, 16, 45))
            .Triage(Jordan, Day(11, 8, 30), Priority.Normal, Trade.Electrical)
            .Reply(Jordan, Day(11, 9, 10), "That pole belongs to the power company, not the building. I've reported it to them and they gave a repair window of five business days.")
            .Cancel(Jordan, Day(11, 9, 12), "The light is on the utility's pole. Reported to the power company; nothing for building maintenance to do."),

        // Closed, then reopened: back with the new requests.
        new RequestScript(Elena, "Treatment room sink drains slowly",
                "The sink in our second treatment room takes several minutes to drain. We wash up between every patient, so it backs up fast.",
                "Suite 202, treatment room 2", Day(14, 8, 15))
            .Triage(Jordan, Day(14, 8, 50), Priority.Normal, Trade.Plumbing)
            .Assign(Jordan, Day(14, 8, 53), Greene)
            .Start(Jordan, Day(13, 10, 0))
            .Complete(Jordan, Day(13, 10, 45), "Greene Valley Plumbing snaked the drain line and cleared a clog about six feet in.")
            .Close(Jordan, Day(11, 9, 0))
            .Says(Day(3, 8, 35), "It's draining slowly again, same sink. It held up for about a week.")
            .Reopen(Jordan, Day(3, 9, 10), "Drain is slow again a week after it was snaked. Needs a camera inspection, not another snake."),

        // Completed, waiting for a manager to verify.
        new RequestScript(Priya, "Blinds won't raise in the conference room",
                "The cord on the big conference room blind snapped. It's stuck all the way down and we have a client presentation later this week.",
                "Suite 201 conference room", Day(8, 9, 25))
            .Triage(Casey, Day(8, 10, 0), Priority.Normal, Trade.General)
            .AssignSelf(Casey, Day(8, 10, 1))
            .Says(Day(7, 8, 45), "Is there any chance this gets done before our client presentation at the end of the week?")
            .Reply(Casey, Day(7, 9, 15), "Yes. The replacement blind arrives tomorrow and I'll hang it first thing the day after.")
            .Start(Casey, Day(4, 8, 20))
            .Complete(Casey, Day(4, 9, 10), "Replaced the conference room blind with a new cordless one. Tested raising and lowering with the tenant present."),
        new RequestScript(Renee, "Waiting room vent blowing cold air",
                "The ceiling vent in our waiting room blows cold air even with the heat on. The rest of the suite is warm.",
                "Suite 301 waiting room", Day(6, 9, 0))
            .Triage(Jordan, Day(6, 9, 30), Priority.High, Trade.HeatingCooling)
            .Assign(Jordan, Day(6, 9, 33), Fairground)
            .Start(Jordan, Day(4, 13, 0))
            .Complete(Jordan, Day(2, 11, 30), "Fairground Heating & Cooling replaced a stuck damper in the branch duct to the waiting room. Supply air measured at 98 degrees."),
        new RequestScript(Marcus, "Loose handrail on the back stairs",
                "The handrail on the back stairwell between the first and second floor wobbles. One of the brackets has pulled out of the wall.",
                "Back stairwell, between floors 1 and 2", Day(7, 14, 20))
            .Triage(Casey, Day(7, 15, 0), Priority.High, Trade.General)
            .AssignSelf(Casey, Day(7, 15, 1))
            .Start(Casey, Day(2, 13, 30))
            .Complete(Casey, Ago(0, 15), "Re-anchored both loose brackets into blocking with new lag bolts. The rail is solid along its full length.").HoldMail(),

        // In progress.
        new RequestScript(Dana, "Suite door sticks in the frame",
                "Our front door has started sticking at the top corner. You have to shoulder it open, and one of our older clients couldn't get in yesterday.",
                "Suite 101 entry door", Day(9, 10, 10))
            .Triage(Jordan, Day(9, 10, 45), Priority.Normal, Trade.General)
            .AssignSelf(Jordan, Day(9, 10, 46))
            .Note(Jordan, Day(9, 11, 0), "Door was rehung two years ago. Check whether the top hinge screws have pulled out of the jamb again.")
            .Start(Jordan, Day(5, 9, 0))
            .Reply(Jordan, Day(5, 9, 40), "I planed the top edge so it opens freely for now. The top hinge needs longer screws and a new mortise, which I'll finish this week."),
        new RequestScript(Thomas, "Outlet sparks when the printer is plugged in",
                "The outlet behind our printer threw a spark and left a scorch mark on the cover plate. We've unplugged everything from it.",
                "Suite 203, copy alcove", Day(4, 8, 10))
            .Triage(Jordan, Day(4, 8, 22), Priority.Urgent, Trade.Electrical)
            .Assign(Jordan, Day(4, 8, 25), Ironwood)
            .Reply(Jordan, Day(4, 8, 30), "Please leave that outlet empty and don't reset the breaker. Ironwood Electric is coming this morning.")
            .Says(Day(4, 8, 41), "Understood. The breaker is off and labeled.")
            .Start(Jordan, Day(4, 10, 15))
            .Note(Jordan, Day(4, 11, 30), "Ironwood found aluminum branch wiring on this circuit. They quoted pigtailing all six outlets in the alcove; approve before they return.")
            .Reply(Jordan, Day(3, 9, 0), "The outlet is made safe and capped. The electrician is coming back to rewire the rest of that circuit, so the alcove outlets stay off until then."),
        new RequestScript(Elena, "Water heater not keeping up",
                "We run out of hot water by late morning. The hydrotherapy tub takes most of it and the sinks go cold afterward.",
                "Suite 202 utility closet", Day(19, 9, 30))
            .Triage(Jordan, Day(19, 10, 15), Priority.High, Trade.Plumbing)
            .Assign(Jordan, Day(19, 10, 18), Greene)
            .Start(Jordan, Day(17, 9, 0))
            .Note(Jordan, Day(17, 11, 20), "Greene Valley says the 40-gallon tank is undersized for the tub. Replacement quote is with the owner for approval.")
            .Reply(Jordan, Day(17, 11, 45), "The plumber confirmed the heater works but is too small for your tub. We're pricing a larger tank and I'll update you when it's approved.")
            .Says(Day(10, 9, 5), "Checking in on the water heater. We're still running out most days.")
            .Reply(Jordan, Day(10, 10, 30), "The larger tank is approved and on order. Delivery is expected within two weeks; installation takes half a day and I'll schedule it around your patients."),
        new RequestScript(Renee, "Light buzzing in the back office",
                "The overhead light in my back office buzzes loudly enough to be picked up on session recordings.",
                "Suite 301 back office", Day(5, 11, 10))
            .Triage(Casey, Day(5, 13, 0), Priority.Normal, Trade.Electrical)
            .Assign(Casey, Day(5, 13, 4), Ironwood)
            .Start(Casey, Ago(0, 40)).HoldMail(),

        // Assigned.
        new RequestScript(Marcus, "Restroom faucet drips",
                "The cold tap in the first-floor restroom drips steadily even when it's shut off hard.",
                "First-floor restroom", Day(20, 13, 10))
            .Triage(Casey, Day(20, 14, 0), Priority.Low, Trade.Plumbing)
            .Note(Casey, Day(20, 14, 5), "Low priority. Bundle with the next Greene Valley visit rather than paying a separate trip charge.")
            .Assign(Jordan, Day(17, 9, 5), Greene)
            .Says(Day(8, 10, 0), "Still dripping. Not urgent, just letting you know.")
            .Reply(Casey, Day(8, 10, 40), "Thanks for the reminder. The plumber will replace the cartridge when they're here to install a water heater upstairs."),
        new RequestScript(Priya, "No heat in the back half of the suite",
                "The two back rooms have had no heat since yesterday afternoon. The front is fine. It was 58 back there when we opened.",
                "Suite 201, back offices", Day(2, 8, 10))
            .Triage(Jordan, Day(2, 8, 24), Priority.Urgent, Trade.HeatingCooling)
            .Assign(Jordan, Day(2, 8, 28), Fairground)
            .Reply(Jordan, Day(2, 8, 35), "Fairground Heating & Cooling's first opening is later this week. I'm bringing up two space heaters now.")
            .Says(Day(2, 9, 15), "Got the heaters, thank you. That will get us through."),
        // Suite 302's one open request; their access is revoked on camera.
        new RequestScript(Sam, "Mail slot flap broken off",
                "The brass flap on our mail slot snapped off its hinge. Mail still fits through but there's a draft.",
                "Suite 302 entry door", Day(4, 15, 20))
            .Triage(Casey, Day(3, 9, 0), Priority.Low, Trade.General)
            .AssignSelf(Casey, Day(3, 9, 1)),
        // Reserved for the stale-page conflict on camera; nothing else touches it.
        new RequestScript(Dana, "Window won't latch in the corner office",
                "The window in the corner office closes but the latch doesn't catch, so it can be pushed open from outside.",
                "Suite 101 corner office", Day(3, 10, 30))
            .Triage(Jordan, Day(3, 11, 0), Priority.Normal, Trade.General)
            .AssignSelf(Jordan, Day(3, 11, 2)),

        // Triaged, not yet assigned.
        new RequestScript(Thomas, "Carpet seam lifting at the entry",
                "The carpet seam just inside our door has come up and is a trip hazard for clients.",
                "Suite 203 entry", Day(3, 14, 0))
            .Triage(Casey, Day(3, 15, 10), Priority.Normal, Trade.General),
        new RequestScript(Marcus, "Parking sign for suite 102 is faded",
                "The reserved-parking sign for our two client spaces is too faded to read and people park there all day.",
                "Front parking lot, spaces 3 and 4", Day(5, 9, 45))
            .Triage(Casey, Day(5, 10, 30), Priority.Low, Trade.General).FailMail(),
        new RequestScript(Renee, "Soap dispenser broken in the third-floor restroom",
                "The wall soap dispenser in the third-floor restroom cracked and leaks onto the counter.",
                "Third-floor restroom", Day(2, 10, 20))
            .Triage(Jordan, Day(2, 11, 0), Priority.Low, Trade.General),

        // Submitted and untouched: the newest requests.
        new RequestScript(Dana, "Ceiling tile cracked above the copier",
            "A ceiling tile over our copier has a crack across it and is sagging at one corner.",
            "Suite 101 copy area", Day(1, 14, 20)),
        new RequestScript(Elena, "Automatic door opener is slow to respond",
            "The push-button door opener at the building's front entrance takes five or six seconds to react. Patients with walkers are giving up and pulling the door.",
            "Main entrance, first floor", Day(1, 16, 5)),
        new RequestScript(Thomas, "Toilet runs constantly in the second-floor restroom",
            "The toilet in the second-floor restroom hasn't stopped running since we got in today.",
            "Second-floor restroom", Ago(1, 40)),
        // Reserved: triaged, assigned, started, completed and closed on camera.
        new RequestScript(Priya, "Kitchenette faucet won't shut off all the way",
            "The faucet in our kitchenette keeps running in a thin stream even with the handle fully closed. We've turned off the valve under the sink for now.",
            "Suite 201 kitchenette", Ago(0, 50)),
    ];
}
