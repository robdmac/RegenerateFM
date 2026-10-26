using System.Collections.Generic;

namespace RegenerateFM.Desktop.FM24
{
    // FM24 24.4.2's memory layouts: the Windows fm.exe (on Windows and under
    // Proton) and the macOS arm64 build. The companion reads the same game
    // objects on both; the Mac build lays some of them out differently (clang
    // orders base classes differently from MSVC, and its heap pointers need all
    // 64 bits).
    //
    // Person-record offsets are from the record that starts with the person's
    // ID twice (<id><id>), 12 bytes into the person object. Addresses are
    // offsets from the image header.
    public sealed class Layout
    {
        public long ManagerGlobal;
        public long NameCellVtable;
        public int RecordYear;          // u16 current game year
        public int RecordBirth;         // u16 day of year, u16 year
        public int RecordName;          // pointer to {u32 length, UTF-8} full name, if the person has one
        // Pointers to the first-name and surname objects, which point to their
        // text: the name of the many people (60% of players in a 2026-10-19
        // Reading save) whose full-name pointer is empty.
        public int FirstNamePart, SurnamePart;
        public bool NameRecordsAligned;  // first/surname records start on 4-byte boundaries
        public int PositionRatings;      // 15 bytes, 1-20
        public int Attributes;           // 54 bytes, 1-100
        public int Height;               // u16 cm
        public int Nation;               // pointer to the nationality's nation object
        public int PlayingClub;          // pointer to the club played for now
        public int Contract;             // pointer to the contract
        public int ContractJob;          // the contract's u16 job code (StaffReader.JobTitle)
        public int PersonFlags;          // a byte whose 0x10 bit marks a woman (ProfileReader.Gender)
        public int Pointer;              // the size of record and object pointers read
        // The person object's class, from its vtable 12 bytes before the record.
        // Player-coaches keep no player ratings or attributes in the record, so
        // they are made as staff.
        public Dictionary<long, string> PersonVtables;
        // Squad rows of players FM24 keeps only briefly ("Scouting Required").
        public HashSet<long> BriefVtables;
        // Player-coaches, which a squad lists but can't describe as players.
        public HashSet<long> PlayerCoachVtables;
        // db::TEAM, db::CLUB (a team's club details) and db::NATION.
        public long TeamVtable, ClubVtable, NationVtable;
        // The open screen's CLIENT_OBJECT_BROWSER and its reference to the object
        // shown: type << 24 | index, 0x01000000 | handle for a person.
        public long ObjectBrowserVtable;
        public int BrowserReference;
        // The global (from the image header) pointing at the game's database,
        // and the slot where its vector of person pointers, indexed by handle,
        // was seen. PersonHandles searches the other slots when it isn't there:
        // on fm.exe the slot changes between runs.
        public long PersonTableGlobal;
        public int PersonTableSlot;
        // The sidebar menu keeps the screen's object reference, also after a
        // skin reload, when the object browser's stops being updated.
        public long SidebarVtable;
        public int SidebarReference;
        public int TableModel;           // a Squad table widget's row model (SquadReader)
        public string ExecutableSuffix;  // the game's executable, lower case

        public static readonly Layout Windows = new Layout
        {
            ManagerGlobal = 0x63635B8,
            NameCellVtable = 0x56807C8,
            RecordYear = 0x36,
            RecordBirth = 0x38,
            RecordName = 0x3C,
            FirstNamePart = 0x4C, SurnamePart = 0x54,
            NameRecordsAligned = true,
            PositionRatings = -0x7C,
            Attributes = -0x6D,
            Height = -0x136,
            Nation = 0x64,
            PlayingClub = -0x154,
            Contract = 0xBC,
            ContractJob = 0x1C,
            PersonFlags = 0x0D,  // as on the Mac, not yet checked on Windows
            Pointer = 4,  // Wine keeps them below 4 GB
            // MSVC RTTI names.
            PersonVtables = new Dictionary<long, string>
            {
                [0x5A4EE78] = "player",  // db::ACTUAL_PLAYER
                [0x5A4C358] = "staff",   // db::ACTUAL_NON_PLAYER
                [0x5C7ECB8] = "staff",   // db::ACTUAL_PLAYER_AND_NON_PLAYER
            },
            BriefVtables = new HashSet<long> { 0x5C7D4F8 },        // db::VIRTUAL_PLAYER
            PlayerCoachVtables = new HashSet<long> { 0x5C7ECB8 },  // db::ACTUAL_PLAYER_AND_NON_PLAYER
            TeamVtable = 0x5A78848,
            ClubVtable = 0x5A51F38,
            NationVtable = 0x5A6C858,
            ObjectBrowserVtable = 0x56BAE48,  // CLIENT_OBJECT_BROWSER, from fm.exe's RTTI
            BrowserReference = 0x880,         // cleared by a skin reload, like the Mac build's
            PersonTableGlobal = 0x6426D90, PersonTableSlot = 0x920,
            SidebarVtable = 0x57E3B48,        // SIDEBAR_MENU_TABLE, as in the DLL
            SidebarReference = 0x280,
            TableModel = 0x6E8,
            ExecutableSuffix = "/fm.exe",
        };

        public static readonly Layout MacOS = new Layout
        {
            ManagerGlobal = 0x525D3D0,
            NameCellVtable = 0x5093B18,
            RecordYear = -0x13A,
            RecordBirth = -0x138,
            RecordName = -0x134,
            // As on Windows, 0x10 and 0x18 after the full-name pointer: the name
            // of most people (16,985 of 30,943 in a 2026-10-21 MK Dons save have
            // no full-name string).
            FirstNamePart = -0x124, SurnamePart = -0x11C,
            NameRecordsAligned = false,  // first/surname records can start anywhere
            PositionRatings = -0x1B4,
            Attributes = -0x1A5,
            Height = -0x26E,
            Nation = -0x10C,
            PlayingClub = -0x28C,
            Contract = -0xB4,
            ContractJob = 0x1C,
            // In a 2026-10-26 MK Dons save: set for 181 staff, every one a woman
            // (Karren Brady, Emma Hayes, Sue Dawson), and none of 4,018 men.
            PersonFlags = 0x0D,
            Pointer = 8,
            // Itanium RTTI names. As on Windows, player-coaches are made as staff.
            PersonVtables = new Dictionary<long, string>
            {
                [0x51E6FD0] = "player",  // db::ACTUAL_PLAYER
                [0x51E1150] = "staff",   // db::ACTUAL_NON_PLAYER
                [0x51ED1C8] = "staff",   // db::ACTUAL_PLAYER_AND_NON_PLAYER
            },
            BriefVtables = new HashSet<long> { 0x521B770 },
            PlayerCoachVtables = new HashSet<long> { 0x51ED1C8 },
            TeamVtable = 0x5216EE8,
            ClubVtable = 0x51F9BA8,
            NationVtable = 0x52099F0,
            ObjectBrowserVtable = 0x4F77650,
            BrowserReference = 0x878,
            PersonTableGlobal = 0x5304848, PersonTableSlot = 0x318,
            SidebarVtable = 0x5060DC0,
            SidebarReference = 0x278,
            TableModel = 0x6C8,
            ExecutableSuffix = "/fm.app/contents/macos/fm",
        };
    }
}
