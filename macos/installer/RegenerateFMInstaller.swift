// SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md).
// RegenerateFM Installer for FM26 and FM24 on macOS.
//
// The app carries the release's FM26-macOS folder and the packaged FM24
// companion (FM24-macOS) in Contents/Resources. For FM26 it copies FM26-macOS
// beside fm.app; for FM24 it copies the companion into the game folder's
// RegenerateFM folder and runs its setup, which installs the Mac library and
// run_regeneratefm24.sh. It then sets the game's Steam launch option as the
// desktop program's SteamLaunch does, so Play in Steam starts RegenerateFM.
// FM24 from the Epic Games Launcher is listed too; for it the companion's
// setup makes run_regeneratefm24.sh the program Epic starts (Epic/
// EpicLaunch.cs), as Epic has no launch options. Install on one FM24 sets up
// every FM24 listed. fm.app itself is never changed. macos/make_installer.py
// builds, signs and packs it.
//
// With --cli it runs one step without a window, for tests:
//   --cli launch-option <localconfig.vdf> <app ID> <prefix> [--remove]
//   --cli install <payload folder> <game folder>
//   --cli uninstall <game folder>
//   --cli needs-change <game folder> [--remove]
//   --cli install-fm24 <packaged companion folder> <FM24 folder>
//   --cli uninstall-fm24 <FM24 folder>
// REGENERATEFM_STEAM_ROOT points it at another Steam folder (and then it
// doesn't check whether Steam is running), REGENERATEFM_EPIC_MANIFESTS at
// another folder of Epic's records, and REGENERATEFM_UNITY_LIBS_SOURCE
// replaces BepInEx's Unity libraries URL.

import AppKit
import SwiftUI

// ---- Steam's launch option (as src/RegenerateFM.Desktop/Steam/SteamLaunch.cs) ----

enum SteamConfig {
    static let appsPath = ["userlocalconfigstore", "software", "valve", "steam", "apps"]
    static let command = "%command%"
    static let backupSuffix = ".regeneratefm-backup"

    struct Token { let kind: UInt8; let start: Int; let end: Int; let value: String }
    struct ConfigError: LocalizedError { let errorDescription: String? }

    /// Each quoted string and brace; // comments are skipped. Works on UTF-8
    /// bytes, so everything outside the changed value is kept byte for byte.
    static func tokens(_ text: [UInt8]) throws -> [Token] {
        var found: [Token] = []
        var index = 0
        while index < text.count {
            let char = text[index]
            if char == UInt8(ascii: "\"") {
                var end = index + 1
                var value: [UInt8] = []
                while end < text.count && text[end] != UInt8(ascii: "\"") {
                    if text[end] == UInt8(ascii: "\\") && end + 1 < text.count { end += 1 }
                    value.append(text[end])
                    end += 1
                }
                if end >= text.count { throw ConfigError(errorDescription: "Unterminated string in localconfig.vdf.") }
                found.append(Token(kind: 0, start: index, end: end + 1, value: String(decoding: value, as: UTF8.self)))
                index = end + 1
            } else if char == UInt8(ascii: "{") || char == UInt8(ascii: "}") {
                found.append(Token(kind: char, start: index, end: index + 1, value: ""))
                index += 1
            } else if char == UInt8(ascii: "/") && index + 1 < text.count && text[index + 1] == UInt8(ascii: "/") {
                index = text[index...].firstIndex(of: UInt8(ascii: "\n")) ?? text.count
            } else {
                index += 1
            }
        }
        return found
    }

    static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }

    /// (open brace end, close brace start, LaunchOptions value token) for the
    /// app's block under apps, or nil if this account doesn't have the game.
    static func appBlock(_ text: [UInt8], appID: String) throws -> (Int, Int, Token?)? {
        var path: [String] = []
        var key: String?
        var block: Int?
        var value: Token?
        for token in try tokens(text) {
            if token.kind == 0 {
                if let name = key {
                    if block != nil && path.count == appsPath.count + 1 && name.lowercased() == "launchoptions" {
                        value = token
                    }
                    key = nil
                } else {
                    key = token.value
                }
            } else if token.kind == UInt8(ascii: "{") {
                guard let name = key else { throw ConfigError(errorDescription: "localconfig.vdf has a block without a name.") }
                path.append(name.lowercased())
                if Array(path.dropLast()) == appsPath && path.last == appID { block = token.end }
                key = nil
            } else {
                if path.isEmpty { throw ConfigError(errorDescription: "localconfig.vdf has an unmatched brace.") }
                if let opened = block, path.count == appsPath.count + 1 { return (opened, token.start, value) }
                path.removeLast()
            }
        }
        return nil
    }

    static func strip(_ text: String) -> String { text.trimmingCharacters(in: .whitespacesAndNewlines) }

    /// The player's launch option with RegenerateFM's prefix in front of %command%.
    static func combined(_ existing: String, _ prefix: String) -> String {
        let existing = strip(existing)
        if existing.contains(prefix) { return existing }
        if let range = existing.range(of: command) {
            return String(existing[..<range.lowerBound]) + "\(prefix) \(command)" + String(existing[range.upperBound...])
        }
        return strip("\(prefix) \(command) \(existing)")
    }

    /// The launch option without RegenerateFM's prefix; empty if nothing else was set.
    static func without(_ existing: String, _ prefix: String) -> String {
        let option = existing.replacingOccurrences(of: prefix, with: "")
            .components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
        return option == command ? "" : option
    }

    /// The file with RegenerateFM's launch option set (or removed), or nil
    /// when this account doesn't have the game.
    static func setLaunchOption(_ text: [UInt8], appID: String, prefix: String, remove: Bool) throws -> [UInt8]? {
        guard let (_, closing, value) = try appBlock(text, appID: appID) else { return nil }
        let existing = value?.value ?? ""
        let option = remove ? without(existing, prefix) : combined(existing, prefix)
        if let value = value {
            return Array(text[..<value.start]) + Array("\"\(escape(option))\"".utf8) + Array(text[value.end...])
        }
        if option.isEmpty { return text }
        let lineStart = (text[..<closing].lastIndex(of: UInt8(ascii: "\n")) ?? -1) + 1
        let before = String(decoding: text[lineStart..<closing], as: UTF8.self)
        let indent = strip(before).isEmpty ? before : ""
        let entry = "\(indent)\t\"LaunchOptions\"\t\t\"\(escape(option))\"\n"
        return Array(text[..<lineStart]) + Array(entry.utf8) + Array(text[lineStart...])
    }
}

// ---- Steam and the game -------------------------------------------------

struct Game: Hashable {
    let name: String
    let appID: String
    let folder: URL

    var isFM24: Bool { appID == Steam.fm24 }
    /// "FM27" or "FM26" (its Demo too), as the installer's text names it.
    var short: String { appID == Steam.games[3].1 ? "FM27" : "FM26" }
    /// Installed by the Epic Games Launcher, not Steam.
    var isEpic: Bool { Epic.installs().contains(realPath(folder)) }
    var launcher: String { isFM24 ? "run_regeneratefm24.sh" : Installer.launcher }
}

/// The path with symbolic links resolved, as realpath(3) gives it
/// (resolvingSymlinksInPath drops /private, which realpath keeps).
func realPath(_ url: URL) -> URL {
    guard let resolved = realpath(url.path, nil) else { return url.standardizedFileURL }
    defer { free(resolved) }
    return URL(fileURLWithPath: String(cString: resolved))
}

/// The Epic Games Launcher's installed games, from its records (as
/// src/RegenerateFM.Desktop/Epic/EpicLaunch.cs reads them).
enum Epic {
    /// Epic's IDs (AppName) for FM24, FM26, the FM26 Demo and the FM27 Launch Preview.
    static let fm24 = "da2038ef19e94554898056d79168b3f8", fm26 = "e54a251079034694b55ab6289707bfa0",
               fm26Demo = "4bec350f11854ea1b6c76969ac88976e", fm27Preview = "8d866ddfb2e142c9b16acb6a08791a97"
    static let backupSuffix = ".regeneratefm-backup"

    static var manifests: URL {
        if let test = ProcessInfo.processInfo.environment["REGENERATEFM_EPIC_MANIFESTS"] { return URL(fileURLWithPath: test) }
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Epic/EpicGamesLauncher/Data/Manifests")
    }

    /// (record, Epic ID, folder, program Epic starts) for every game Epic has
    /// installed, wherever the player chose.
    static func records() -> [(record: URL, app: String, folder: URL, program: String)] {
        let files = (try? FileManager.default.contentsOfDirectory(at: manifests, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "item" }.sorted { $0.path < $1.path }.compactMap { record in
            guard let data = try? Data(contentsOf: record),
                  let item = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let location = item["InstallLocation"] as? String, !location.isEmpty else { return nil }
            return (record, item["AppName"] as? String ?? "", realPath(URL(fileURLWithPath: location)), item["LaunchExecutable"] as? String ?? "")
        }
    }

    /// Every game folder Epic has installed.
    static func installs() -> [URL] { records().map(\.folder) }

    /// FM24, FM26 and FM27 among them, by Epic's IDs.
    static func installedGames() -> [Game] {
        records().compactMap { entry in
            guard FileManager.default.fileExists(atPath: entry.folder.appendingPathComponent("fm.app").path) else { return nil }
            switch entry.app {
            case fm24: return Game(name: "Football Manager 2024 (Epic)", appID: Steam.fm24, folder: entry.folder)
            case fm26: return Game(name: "Football Manager 26 (Epic)", appID: Steam.games[0].1, folder: entry.folder)
            case fm26Demo: return Game(name: "Football Manager 26 Demo (Epic)", appID: Steam.games[1].1, folder: entry.folder)
            case fm27Preview: return Game(name: "Football Manager 27 (Epic)", appID: Steam.games[3].1, folder: entry.folder)
            // FM27 itself, once Epic sells it under its own ID: by its program.
            case _ where gameProgram(entry.folder).hasSuffix("/Football Manager 27"):
                return Game(name: "Football Manager 27 (Epic)", appID: Steam.games[3].1, folder: entry.folder)
            default: return nil
            }
        }
    }

    /// Whether the Epic Games Launcher is running; it may write back the records it read.
    static func running() -> Bool {
        NSWorkspace.shared.runningApplications.contains { $0.executableURL?.lastPathComponent == "EpicGamesLauncher-Mac-Shipping" }
    }

    /// The game's own program in fm.app, as Epic starts it.
    static func gameProgram(_ folder: URL) -> String {
        let info = NSDictionary(contentsOf: folder.appendingPathComponent("fm.app/Contents/Info.plist"))
        return "fm.app/Contents/MacOS/" + (info?["CFBundleExecutable"] as? String ?? "fm")
    }

    /// Whether Epic's record for FM26 in ``folder`` doesn't start what RegenerateFM wants yet.
    static func needsChange(_ game: Game, remove: Bool) -> Bool {
        guard let entry = records().first(where: { $0.folder == realPath(game.folder) }) else { return false }
        return entry.program != (remove ? gameProgram(game.folder) : game.launcher)
    }

    /// Makes Epic start the game's launcher (or, removing, the game itself), as
    /// src/RegenerateFM.Desktop/Epic/EpicLaunch.cs does for FM24: only while
    /// Epic is closed, keeping a copy of the record the first time.
    static func update(_ game: Game, remove: Bool) throws {
        guard let entry = records().first(where: { $0.folder == realPath(game.folder) }) else { return }
        let data = try Data(contentsOf: entry.record)
        guard var item = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw SteamConfig.ConfigError(errorDescription: "Epic's record of \(game.name) couldn't be read.")
        }
        item["LaunchExecutable"] = remove ? gameProgram(game.folder) : game.launcher
        let backup = URL(fileURLWithPath: entry.record.path + backupSuffix)
        if !FileManager.default.fileExists(atPath: backup.path) { try data.write(to: backup) }
        try JSONSerialization.data(withJSONObject: item, options: [.prettyPrinted, .sortedKeys]).write(to: entry.record, options: .atomic)
    }
}

enum Steam {
    static let fm24 = "2252570"
    static let games = [("Football Manager 26", "3551340"), ("Football Manager 26 Demo", "3551360"),
                        ("Football Manager 2024", fm24), ("Football Manager 27", "4530370")]
    static let testRoot = ProcessInfo.processInfo.environment["REGENERATEFM_STEAM_ROOT"]

    static var root: URL {
        if let testRoot = testRoot { return URL(fileURLWithPath: testRoot) }
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Steam")
    }

    /// Every Steam library folder (the ones holding steamapps).
    static func libraries() -> [URL] {
        var candidates = [root]
        if let text = try? String(contentsOf: root.appendingPathComponent("steamapps/libraryfolders.vdf"), encoding: .utf8),
           let pattern = try? NSRegularExpression(pattern: "\"path\"\\s+\"([^\"]+)\"") {
            for match in pattern.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                if let range = Range(match.range(at: 1), in: text) {
                    candidates.append(URL(fileURLWithPath: text[range].replacingOccurrences(of: "\\\\", with: "\\")))
                }
            }
        }
        var found: [URL] = []
        for library in candidates {
            let resolved = realPath(library)
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: resolved.appendingPathComponent("steamapps").path, isDirectory: &isDir),
               isDir.boolValue, !found.contains(resolved) {
                found.append(resolved)
            }
        }
        return found
    }

    static func installedGames() -> [Game] {
        var found: [Game] = []
        for library in libraries() {
            for (name, appID) in games {
                let folder = library.appendingPathComponent("steamapps/common/\(name)")
                if FileManager.default.fileExists(atPath: folder.appendingPathComponent("fm.app").path) {
                    found.append(Game(name: name, appID: appID, folder: folder))
                }
            }
        }
        return found
    }

    static func running() -> Bool {
        if testRoot != nil { return false }
        let pgrep = Process()
        pgrep.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        pgrep.arguments = ["-x", "steam_osx"]
        pgrep.standardOutput = FileHandle.nullDevice
        try? pgrep.run()
        pgrep.waitUntilExit()
        return pgrep.terminationStatus == 0
    }

    static func prefix(_ game: Game) -> String {
        "\"\(realPath(game.folder).path)/\(game.launcher)\""
    }

    static func manualText(_ game: Game) -> String { "\(prefix(game)) \(SteamConfig.command)" }

    static func configs() -> [URL] {
        let userdata = root.appendingPathComponent("userdata")
        let accounts = (try? FileManager.default.contentsOfDirectory(atPath: userdata.path)) ?? []
        return accounts.sorted()
            .map { userdata.appendingPathComponent("\($0)/config/localconfig.vdf") }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// True if some account's launch option isn't as RegenerateFM wants it yet.
    static func needsChange(_ game: Game, remove: Bool) -> Bool {
        configs().contains { config in
            do {
                let text = try [UInt8](Data(contentsOf: config))
                // nil: this account doesn't have the game.
                guard let updated = try SteamConfig.setLaunchOption(text, appID: game.appID, prefix: prefix(game),
                                                                    remove: remove) else { return false }
                return updated != text
            } catch {
                return true
            }
        }
    }

    /// Changes every account's localconfig.vdf that has the game; the number changed.
    static func update(_ game: Game, remove: Bool) throws -> Int {
        var changed = 0
        for config in configs() {
            let text = try [UInt8](Data(contentsOf: config))
            guard let updated = try SteamConfig.setLaunchOption(text, appID: game.appID, prefix: prefix(game),
                                                                remove: remove), updated != text else { continue }
            let backup = URL(fileURLWithPath: config.path + SteamConfig.backupSuffix)
            if !FileManager.default.fileExists(atPath: backup.path) {
                try FileManager.default.copyItem(at: config, to: backup)
            }
            let temporary = URL(fileURLWithPath: config.path + ".regeneratefm-tmp")
            try Data(updated).write(to: temporary)
            _ = try FileManager.default.replaceItemAt(config, withItemAt: temporary)
            changed += 1
        }
        return changed
    }
}

// ---- Installing ---------------------------------------------------------

enum Installer {
    static let launcher = "run_regeneratefm.sh"
    static let plugin = "BepInEx/plugins/RegenerateFM"
    /// What the FM26-macOS folder adds beside fm.app. Faces and settings are
    /// in FM26's user-data folder, so removing these keeps them.
    /// What to expect from FM26's first start after installing (as the Linux installer says).
    static let firstStartNote = "The first time FM26 starts with RegenerateFM, it takes a few minutes longer (up to about 10 on a slower computer) while BepInEx prepares the game; a RegenerateFM window shows its progress. Let it finish. This happens once: later starts are as quick as before. It happens once more after an FM26 update."

    static let added = ["BepInEx", "dotnet", "libdoorstop.dylib", ".doorstop_version", "clear-exception-ports", "regeneratefm-progress", launcher]

    static var payload: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("FM26-macOS")
    }

    /// FM24: the packaged companion (dotnet publish src/RegenerateFM.Desktop -r osx-arm64), and where it goes.
    static let companionName = "regeneratefm-fm24"
    static var fm24Payload: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("FM24-macOS/\(companionName)")
    }
    static func companionFolder(_ game: URL) -> URL { game.appendingPathComponent("RegenerateFM/\(companionName)") }
    static func companion(_ game: URL) -> URL { companionFolder(game).appendingPathComponent(companionName) }

    static var appleSilicon: Bool {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        return sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 && value == 1
    }

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    static func isInstalled(_ game: Game) -> Bool {
        if game.isFM24 {
            return FileManager.default.fileExists(atPath: companion(game.folder).path)
                && FileManager.default.fileExists(atPath: game.folder.appendingPathComponent("libregeneratefm24.dylib").path)
        }
        return FileManager.default.fileExists(atPath: game.folder.appendingPathComponent("\(plugin)/RegenerateFM.dll").path)
    }

    static func gameRunning(_ folder: URL) -> Bool {
        let app = realPath(folder.appendingPathComponent("fm.app")).path
        return NSWorkspace.shared.runningApplications.contains { $0.bundleURL.map { realPath($0).path } == app }
    }

    static func rosettaInstalled() -> Bool {
        let arch = Process()
        arch.executableURL = URL(fileURLWithPath: "/usr/bin/arch")
        arch.arguments = ["-x86_64", "/usr/bin/true"]
        arch.standardError = FileHandle.nullDevice
        do { try arch.run() } catch { return false }
        arch.waitUntilExit()
        return arch.terminationStatus == 0
    }

    /// Copies the payload into the game folder, replacing what is there; the
    /// number of files copied.
    static func copyFiles(from payload: URL, to folder: URL) throws -> Int {
        let manager = FileManager.default
        guard manager.fileExists(atPath: folder.appendingPathComponent("fm.app").path) else {
            throw SteamConfig.ConfigError(errorDescription: "\(folder.path) has no fm.app. Choose the folder that holds the game's fm.app.")
        }
        guard let walker = manager.enumerator(at: payload, includingPropertiesForKeys: [.isDirectoryKey]) else {
            throw SteamConfig.ConfigError(errorDescription: "The installer's files are missing.")
        }
        var copied = 0
        let base = realPath(payload).path
        for case let source as URL in walker {
            let relative = String(realPath(source).path.dropFirst(base.count + 1))
            let target = folder.appendingPathComponent(relative)
            if (try source.resourceValues(forKeys: [.isDirectoryKey])).isDirectory == true {
                try manager.createDirectory(at: target, withIntermediateDirectories: true)
                continue
            }
            if manager.fileExists(atPath: target.path) || (try? manager.destinationOfSymbolicLink(atPath: target.path)) != nil {
                try manager.removeItem(at: target)
            }
            try manager.copyItem(at: source, to: target)
            // A library macOS thinks was downloaded isn't loaded unless notarised.
            removexattr(target.path, "com.apple.quarantine", XATTR_NOFOLLOW)
            copied += 1
        }
        // BepInEx looks for the game's Data folder beside fm.app (run_regeneratefm.sh makes it too).
        let data = folder.appendingPathComponent("Data")
        if !manager.fileExists(atPath: data.path) && (try? manager.destinationOfSymbolicLink(atPath: data.path)) == nil {
            try manager.createSymbolicLink(atPath: data.path, withDestinationPath: "fm.app/Contents/Resources/Data")
        }
        return copied
    }

    /// FM24: copies the companion into the game folder (replacing an earlier
    /// one; setup keeps FM24's token) and runs its setup, which installs the
    /// Mac library, run_regeneratefm24.sh and the RegenerateFM skin. The setup's output.
    static func installFM24(from payload: URL, to folder: URL) throws -> String {
        let manager = FileManager.default
        guard manager.fileExists(atPath: folder.appendingPathComponent("fm.app").path) else {
            throw SteamConfig.ConfigError(errorDescription: "\(folder.path) has no fm.app. Choose the folder that holds FM24's fm.app.")
        }
        let target = companionFolder(folder)
        if manager.fileExists(atPath: target.path) { try manager.removeItem(at: target) }
        try manager.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try manager.copyItem(at: payload, to: target)
        if let walker = manager.enumerator(atPath: target.path) {
            for case let name as String in walker {
                removexattr(target.appendingPathComponent(name).path, "com.apple.quarantine", XATTR_NOFOLLOW)
            }
        }
        // The setup sets what Epic starts; Steam's launch option is set here.
        let epic = Epic.installs().contains(realPath(folder))
        return try runCompanion(folder, ["setup", "--game-dir", folder.path] + (epic ? [] : ["--no-launch-option"]))
    }

    /// FM24: the companion's setup --remove (the library, launcher and
    /// skin panels), then the companion itself.
    static func removeFM24(from folder: URL) throws -> String {
        if FileManager.default.fileExists(atPath: companion(folder).path) {
            let epic = Epic.installs().contains(realPath(folder))
            _ = try runCompanion(folder, ["setup", "--game-dir", folder.path, "--remove"] + (epic ? [] : ["--no-launch-option"]))
        }
        let ours = folder.appendingPathComponent("RegenerateFM")
        if FileManager.default.fileExists(atPath: ours.path) { try FileManager.default.removeItem(at: ours) }
        return "Removed RegenerateFM from \(folder.lastPathComponent). Your faces are kept in FM24's user-data folder."
    }

    static func runCompanion(_ folder: URL, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = companion(folder)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        process.standardInput = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let text = String(decoding: data, as: UTF8.self)
            .replacingOccurrences(of: "Press Enter to close.", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
        if process.terminationStatus != 0 {
            let reason = text.components(separatedBy: "\n").last(where: { !$0.isEmpty }) ?? "it stopped with code \(process.terminationStatus)"
            throw SteamConfig.ConfigError(errorDescription: reason)
        }
        return text
    }

    /// BepInEx makes FM26's interop assemblies on the game's first start, from
    /// Unity's base libraries, which it downloads from unity.bepinex.dev. If
    /// that site doesn't answer, FM26 starts without RegenerateFM and only
    /// BepInEx's log says why. BepInEx uses a zip already in BepInEx/unity-libs
    /// instead, so the installer downloads it first (as the Linux installer's
    /// UnityLibraries does). What happened, or nil if the zip was there.
    static func fetchUnityLibraries(_ folder: URL) -> String? {
        guard let version = unityVersion(folder) else {
            return "Couldn't read the game's Unity version, so BepInEx will download Unity's libraries itself when the game first starts."
        }
        let zip = folder.appendingPathComponent("BepInEx/unity-libs/\(version).zip")
        if FileManager.default.fileExists(atPath: zip.path) { return nil }
        let source = (ProcessInfo.processInfo.environment["REGENERATEFM_UNITY_LIBS_SOURCE"]
            ?? "https://unity.bepinex.dev/libraries/{VERSION}.zip").replacingOccurrences(of: "{VERSION}", with: version)
        let failed = "BepInEx tries again when the game starts; if RegenerateFM's commands are missing in it, click Update again later."
        guard let url = URL(string: source) else { return "Couldn't download Unity's libraries for BepInEx from \(source). \(failed)" }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForResource = 60
        var result: Result<Data, Error> = .failure(URLError(.unknown))
        let done = DispatchSemaphore(value: 0)
        URLSession(configuration: configuration).dataTask(with: url) { data, response, error in
            if let error = error {
                result = .failure(error)
            } else if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                result = .failure(URLError(.badServerResponse, userInfo: [NSLocalizedDescriptionKey: "HTTP \(http.statusCode)"]))
            } else {
                result = .success(data ?? Data())
            }
            done.signal()
        }.resume()
        done.wait()
        do {
            let data = try result.get()
            guard data.starts(with: [0x50, 0x4b, 3, 4]) else {
                return "Unity's libraries from \(source) aren't a ZIP file, so BepInEx will download them itself when the game first starts."
            }
            try FileManager.default.createDirectory(at: zip.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: zip, options: .atomic)
        } catch {
            return "Couldn't download Unity's libraries for BepInEx from \(source) (\(error.localizedDescription)). \(failed)"
        }
        return "Downloaded Unity's libraries for BepInEx (\(version))."
    }

    /// FM26's Unity version as BepInEx names its zip ("6000.0.52" for
    /// 6000.0.52f1), from the header of the game's globalgamemanagers.
    static func unityVersion(_ folder: URL) -> String? {
        let path = folder.appendingPathComponent("fm.app/Contents/Resources/Data/globalgamemanagers")
        guard let handle = try? FileHandle(forReadingFrom: path), let header = try? handle.read(upToCount: 256) else { return nil }
        try? handle.close()
        let text = String(decoding: header.map { $0 < 0x80 ? $0 : 0x20 }, as: UTF8.self)
        guard let regex = try? NSRegularExpression(pattern: "(\\d+)\\.(\\d+)\\.(\\d+)[abfp]\\d+"),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        let parts = (1...3).compactMap { Range(match.range(at: $0), in: text).map { String(text[$0]) } }
        return parts.joined(separator: ".")
    }

    /// Removes what copyFiles added. Another mod's BepInEx plugins are kept,
    /// with BepInEx, and then only RegenerateFM's plugin goes.
    static func removeFiles(from folder: URL) throws -> String {
        let manager = FileManager.default
        let plugins = folder.appendingPathComponent("BepInEx/plugins")
        let others = ((try? manager.contentsOfDirectory(atPath: plugins.path)) ?? [])
            .filter { $0 != "RegenerateFM" && !$0.hasPrefix(".") }
        if !others.isEmpty {
            let ours = folder.appendingPathComponent(plugin)
            if manager.fileExists(atPath: ours.path) { try manager.removeItem(at: ours) }
            return "Removed the RegenerateFM plugin. BepInEx was left in place because it has other plugins: "
                + others.sorted().joined(separator: ", ") + "."
        }
        for name in added {
            let path = folder.appendingPathComponent(name)
            if manager.fileExists(atPath: path.path) { try manager.removeItem(at: path) }
        }
        let data = folder.appendingPathComponent("Data")
        if (try? manager.destinationOfSymbolicLink(atPath: data.path)) == "fm.app/Contents/Resources/Data" {
            try manager.removeItem(at: data)
        }
        return "Removed RegenerateFM from \(folder.lastPathComponent). Your faces are kept in the game's user-data folder."
    }
}

// ---- The window ---------------------------------------------------------

final class Model: ObservableObject {
    @Published var games: [Game] = []
    @Published var chosen: Game?
    @Published var message = ""
    @Published var manual: String?
    @Published var working = false
    @Published var rosetta = Installer.rosettaInstalled()

    init() { refresh() }

    func refresh() {
        games = Steam.installedGames() + Epic.installedGames()
        if chosen == nil || !games.contains(chosen!) && !FileManager.default.fileExists(atPath: chosen!.folder.path) {
            chosen = games.first
        }
        if chosen == nil {
            message = "Football Manager 27, Football Manager 26 or Football Manager 2024 wasn't found in your Steam libraries or Epic's games. Choose the game's folder (in Steam: Manage > Browse local files)."
        }
    }

    func choose() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.message = "Choose the folder that holds the game's fm.app"
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        guard FileManager.default.fileExists(atPath: folder.appendingPathComponent("fm.app").path) else {
            message = "That folder has no fm.app. In Steam, use Manage > Browse local files on the game."
            return
        }
        // FM24's folder holds its data beside fm.app; FM26's is inside the app.
        let fm24 = FileManager.default.fileExists(atPath: folder.appendingPathComponent("data/game/panels.fmf").path)
        let demo = folder.lastPathComponent.localizedCaseInsensitiveContains("demo")
        let fm27 = FileManager.default.fileExists(atPath: folder.appendingPathComponent("fm.app/Contents/MacOS/Football Manager 27").path)
        let (name, appID) = Steam.games[fm24 ? 2 : fm27 ? 3 : demo ? 1 : 0]
        chosen = Game(name: name, appID: appID, folder: folder)
        if let epic = Epic.installedGames().first(where: { $0.folder == realPath(folder) }) { chosen = epic }
        if !games.contains(chosen!) { games.append(chosen!) }
        message = ""
    }

    func install() {
        guard let game = chosen else { return }
        if Installer.gameRunning(game.folder) { message = "Quit \(game.name) first."; return }
        manual = nil
        if game.isFM24 {
            guard Installer.appleSilicon else { message = "RegenerateFM for FM24 needs an Apple Silicon Mac."; return }
            guard let payload = Installer.fm24Payload else { message = "The installer's files are missing."; return }
            // Every FM24 here (Steam's and Epic's), so either starts RegenerateFM.
            let targets = [game] + games.filter { $0.isFM24 && realPath($0.folder) != realPath(game.folder) }
            if let running = targets.first(where: { Installer.gameRunning($0.folder) }) { message = "Quit \(running.name) first."; return }
            working = true
            message = "Setting RegenerateFM up for \(targets.map(\.name).joined(separator: " and "))…"
            DispatchQueue.global().async {
                let results = targets.map { target in (target, Result { try Installer.installFM24(from: payload, to: target.folder) }) }
                DispatchQueue.main.async {
                    self.working = false
                    var parts: [String] = []
                    for (target, result) in results {
                        switch result {
                        case .success: parts.append(self.installedText(target))
                        case .failure(let error): parts.append("Couldn't set RegenerateFM up for \(target.name): \(error.localizedDescription)")
                        }
                    }
                    if results.contains(where: { if case .success = $0.1 { return true } else { return false } }) {
                        parts.append("In FM24, Generate new face and Update face are in a profile's Overview tab menu; for them on the portrait too, choose the RegenerateFM skin in Preferences > Interface.")
                    }
                    self.message = parts.joined(separator: "\n\n")
                }
            }
            return
        }
        guard let payload = Installer.payload else { message = "The installer's files are missing."; return }
        working = true
        message = "Installing RegenerateFM in \(game.name)…"
        DispatchQueue.global().async {
            let result = Result { () -> String? in
                _ = try Installer.copyFiles(from: payload, to: game.folder)
                return Installer.fetchUnityLibraries(game.folder)
            }
            DispatchQueue.main.async {
                self.working = false
                switch result {
                case .success(let libraries): self.finishInstall(game, note: libraries)
                case .failure(let error): self.message = "Couldn't copy RegenerateFM into \(game.name): \(error.localizedDescription)"
                }
            }
        }
    }

    /// With the files in place: the launch option, and what to do next.
    private func finishInstall(_ game: Game, note: String? = nil) {
        var text = installedText(game, note: note)
        text += " " + Installer.firstStartNote.replacingOccurrences(of: "FM26", with: game.short)
        if !rosetta {
            text += " \(game.short) runs under Rosetta 2 with RegenerateFM: click Install Rosetta first."
        }
        message = text
    }

    /// That RegenerateFM is installed in the game, and its launch option or
    /// what Epic starts; sets ``manual`` when the player must set it.
    private func installedText(_ game: Game, note: String? = nil) -> String {
        var text = "RegenerateFM \(Installer.version) is installed in \(game.name)."
        if let note = note, !note.hasPrefix("Downloaded") { text += " " + note }
        if game.isEpic {
            // FM24's companion setup has already set what Epic starts.
            if !game.isFM24 && Epic.needsChange(game, remove: false) {
                if Epic.running() {
                    return text + " To finish, quit the Epic Games Launcher (Epic Games Launcher > Quit Epic Games Launcher) and click Update again, so the installer can make Epic start RegenerateFM."
                }
                do { try Epic.update(game, remove: false) } catch {
                    return text + " Epic's setting for the game couldn't be changed (\(error.localizedDescription)), so Launch in Epic starts it without RegenerateFM."
                }
            }
            text += " Epic now starts it with RegenerateFM: press Launch in the Epic Games Launcher. After an Epic update or Verify, click Update again."
        } else if !Steam.needsChange(game, remove: false) {
            text += " Its Steam launch option is already set: press Play in Steam."
        } else if Steam.running() {
            text += " To finish, quit Steam (Steam > Quit Steam) and click Update again, so the installer can set the game's launch option. Or set it yourself in Steam (\(game.name) > Properties > General > Launch Options):"
            manual = Steam.manualText(game)
        } else {
            do {
                let changed = try Steam.update(game, remove: false)
                text += changed > 0 ? " Its Steam launch option is set: start Steam and press Play."
                    : " No Steam account here had \(game.name)'s settings yet. Set the launch option yourself in Steam (Properties > General > Launch Options):"
                if changed == 0 { manual = Steam.manualText(game) }
            } catch {
                text += " Steam's launch option couldn't be set (\(error.localizedDescription)). Set it yourself in Steam (Properties > General > Launch Options):"
                manual = Steam.manualText(game)
            }
        }
        return text
    }

    func uninstall() {
        guard let game = chosen else { return }
        if Installer.gameRunning(game.folder) { message = "Quit \(game.name) first."; return }
        manual = nil
        // Without the launcher, Steam or Epic couldn't start the game with its
        // setting still pointing there. For Epic's FM24 the companion's setup
        // puts the game back; for FM26 the installer does.
        if game.isEpic && !game.isFM24 && Epic.needsChange(game, remove: true) {
            if Epic.running() {
                message = "Quit the Epic Games Launcher first (Epic Games Launcher > Quit Epic Games Launcher), so the installer can make Epic start the game itself again, then click Uninstall again."
                return
            }
            do { try Epic.update(game, remove: true) } catch {
                message = "Couldn't change Epic's setting for the game (\(error.localizedDescription))."
                return
            }
        }
        if !game.isEpic && Steam.needsChange(game, remove: true) {
            if Steam.running() {
                message = "Quit Steam first (Steam > Quit Steam), so the installer can take RegenerateFM out of the game's launch option, then click Uninstall again."
                return
            }
            do { _ = try Steam.update(game, remove: true) } catch {
                message = "Couldn't change Steam's launch option (\(error.localizedDescription)). Remove \(Steam.manualText(game)) from Launch Options in Steam, then click Uninstall again."
                return
            }
        }
        do {
            message = game.isFM24 ? try Installer.removeFM24(from: game.folder) : try Installer.removeFiles(from: game.folder)
        } catch {
            message = "Couldn't remove RegenerateFM: \(error.localizedDescription)"
        }
    }

    func installRosetta() {
        working = true
        DispatchQueue.global().async {
            var error: NSDictionary?
            NSAppleScript(source: "do shell script \"/usr/sbin/softwareupdate --install-rosetta --agree-to-license\" with administrator privileges")?
                .executeAndReturnError(&error)
            DispatchQueue.main.async {
                self.working = false
                self.rosetta = Installer.rosettaInstalled()
                self.message = self.rosetta ? "Rosetta 2 is installed." : "Rosetta 2 wasn't installed. In Terminal, run: softwareupdate --install-rosetta"
            }
        }
    }
}

struct InstallerView: View {
    @ObservedObject var model: Model

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("RegenerateFM \(Installer.version)").font(.title2).bold()
            Text("Adds RegenerateFM to Football Manager 27, Football Manager 26 and Football Manager 2024: Generate face and Update face in the game's menus. fm.app itself isn't changed.")
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Picker("Game:", selection: $model.chosen) {
                    ForEach(model.games, id: \.self) { game in
                        Text(game.name + (Installer.isInstalled(game) ? " (installed)" : "")).tag(Optional(game))
                    }
                }
                .disabled(model.games.isEmpty)
                Button("Choose folder…") { model.choose() }
            }
            if let game = model.chosen {
                Text(game.folder.path).font(.caption).foregroundColor(.secondary).textSelection(.enabled)
            }
            HStack {
                Button(model.chosen.map { Installer.isInstalled($0) } == true ? "Update" : "Install") { model.install() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.chosen == nil || model.working)
                Button("Uninstall") { model.uninstall() }
                    .disabled(model.chosen.map { !Installer.isInstalled($0) } ?? true || model.working)
                if !model.rosetta && model.chosen?.isFM24 != true {
                    Button("Install Rosetta") { model.installRosetta() }.disabled(model.working)
                }
            }
            if !model.message.isEmpty {
                Text(model.message).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            }
            if let manual = model.manual {
                HStack {
                    Text(manual).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                    Button("Copy") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(manual, forType: .string)
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .padding(20)
        .frame(width: 560)
        .frame(minHeight: 280)
    }
}

struct InstallerApp: App {
    @StateObject private var model = Model()
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        WindowGroup("RegenerateFM Installer") { InstallerView(model: model) }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

// ---- Entry point --------------------------------------------------------

func runCommandLine(_ arguments: [String]) -> Int32 {
    do {
        switch arguments.first {
        case "launch-option" where arguments.count >= 4:
            let text = try [UInt8](Data(contentsOf: URL(fileURLWithPath: arguments[1])))
            guard let updated = try SteamConfig.setLaunchOption(text, appID: arguments[2], prefix: arguments[3],
                                                                remove: arguments.contains("--remove")) else {
                print("NONE")
                return 0
            }
            FileHandle.standardOutput.write(Data(updated))
        case "install" where arguments.count == 3:
            let folder = URL(fileURLWithPath: arguments[2])
            print("copied \(try Installer.copyFiles(from: URL(fileURLWithPath: arguments[1]), to: folder))")
            if let libraries = Installer.fetchUnityLibraries(folder) { print(libraries) }
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: folder)
            // Only a test Steam folder's launch options are changed from the command line;
            // what Epic starts is, for Epic's FM26 (with Epic closed).
            if Steam.testRoot != nil { print("launch options changed \(try Steam.update(game, remove: false))") }
            if game.isEpic && Epic.needsChange(game, remove: false) {
                if Epic.running() { throw SteamConfig.ConfigError(errorDescription: "Quit the Epic Games Launcher first.") }
                try Epic.update(game, remove: false)
                print("Epic starts \(game.launcher)")
            }
        case "uninstall" where arguments.count == 2:
            let folder = URL(fileURLWithPath: arguments[1])
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: folder)
            if Steam.testRoot != nil { print("launch options changed \(try Steam.update(game, remove: true))") }
            if game.isEpic && Epic.needsChange(game, remove: true) {
                if Epic.running() { throw SteamConfig.ConfigError(errorDescription: "Quit the Epic Games Launcher first.") }
                try Epic.update(game, remove: true)
                print("Epic starts \(Epic.gameProgram(folder))")
            }
            print(try Installer.removeFiles(from: folder))
        case "install-fm24" where arguments.count == 3:
            print(try Installer.installFM24(from: URL(fileURLWithPath: arguments[1]), to: URL(fileURLWithPath: arguments[2])))
        case "uninstall-fm24" where arguments.count == 2:
            print(try Installer.removeFM24(from: URL(fileURLWithPath: arguments[1])))
        case "needs-change" where arguments.count >= 2:
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: URL(fileURLWithPath: arguments[1]))
            print(Steam.needsChange(game, remove: arguments.contains("--remove")))
        case "games":
            for game in Steam.installedGames() + Epic.installedGames() {
                print("\(game.appID) \(game.folder.path)\(game.isEpic ? " epic" : "")")
            }
        default:
            FileHandle.standardError.write(Data("Unknown command\n".utf8))
            return 2
        }
        return 0
    } catch {
        FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
        return 1
    }
}

@main
enum Main {
    static func main() {
        let arguments = Array(CommandLine.arguments.dropFirst())
        if arguments.first == "--cli" { exit(runCommandLine(Array(arguments.dropFirst()))) }
        InstallerApp.main()
    }
}
