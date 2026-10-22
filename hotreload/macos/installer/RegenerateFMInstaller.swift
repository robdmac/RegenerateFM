// SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md).
// RegenerateFM Installer for FM26 on macOS.
//
// The app carries the release's FM26-macOS folder in Contents/Resources and
// copies it beside fm.app, then sets FM26's Steam launch option the way
// steam_launch.py does, so Play in Steam starts RegenerateFM. fm.app itself is
// never changed. hotreload/macos/make_installer.py builds, signs and packs it.
//
// With --cli it runs one step without a window, for tests:
//   --cli launch-option <localconfig.vdf> <app ID> <prefix> [--remove]
//   --cli install <payload folder> <game folder>
//   --cli uninstall <game folder>
//   --cli needs-change <game folder> [--remove]
// REGENERATEFM_STEAM_ROOT points it at another Steam folder (and then it
// doesn't check whether Steam is running).

import AppKit
import SwiftUI

// ---- Steam's launch option (steam_launch.py) ----------------------------

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
}

/// The path with symbolic links resolved, as realpath(3) gives it
/// (resolvingSymlinksInPath drops /private, which Python's resolve() keeps).
func realPath(_ url: URL) -> URL {
    guard let resolved = realpath(url.path, nil) else { return url.standardizedFileURL }
    defer { free(resolved) }
    return URL(fileURLWithPath: String(cString: resolved))
}

enum Steam {
    static let games = [("Football Manager 26", "3551340"), ("Football Manager 26 Demo", "3551360")]
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
        "\"\(realPath(game.folder).path)/\(Installer.launcher)\""
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
    static let plugin = "BepInEx/plugins/RegenerateFMHotReload"
    /// What the FM26-macOS folder adds beside fm.app. Faces and settings are
    /// in FM26's user-data folder, so removing these keeps them.
    static let added = ["BepInEx", "dotnet", "libdoorstop.dylib", ".doorstop_version", launcher]

    static var payload: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("FM26-macOS")
    }

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    static func isInstalled(_ folder: URL) -> Bool {
        FileManager.default.fileExists(atPath: folder.appendingPathComponent("\(plugin)/RegenerateFMHotReload.dll").path)
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

    /// Copies the payload into the game folder, replacing what an earlier
    /// version put there; the number of files copied.
    static func copyFiles(from payload: URL, to folder: URL) throws -> Int {
        let manager = FileManager.default
        guard manager.fileExists(atPath: folder.appendingPathComponent("fm.app").path) else {
            throw SteamConfig.ConfigError(errorDescription: "\(folder.path) has no fm.app. Choose the folder that holds FM26's fm.app.")
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

    /// Removes what copyFiles added. Another mod's BepInEx plugins are kept,
    /// with BepInEx, and then only RegenerateFM's plugin goes.
    static func removeFiles(from folder: URL) throws -> String {
        let manager = FileManager.default
        let plugins = folder.appendingPathComponent("BepInEx/plugins")
        let others = ((try? manager.contentsOfDirectory(atPath: plugins.path)) ?? [])
            .filter { $0 != "RegenerateFMHotReload" && !$0.hasPrefix(".") }
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
        return "Removed RegenerateFM from \(folder.lastPathComponent). Your faces are kept in FM26's user-data folder."
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
        games = Steam.installedGames()
        if chosen == nil || !games.contains(chosen!) && !FileManager.default.fileExists(atPath: chosen!.folder.path) {
            chosen = games.first
        }
        if chosen == nil {
            message = "Football Manager 26 wasn't found in your Steam libraries. Choose its folder (in Steam: Manage > Browse local files)."
        }
    }

    func choose() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.message = "Choose the folder that holds FM26's fm.app"
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        guard FileManager.default.fileExists(atPath: folder.appendingPathComponent("fm.app").path) else {
            message = "That folder has no fm.app. In Steam, use Manage > Browse local files on Football Manager 26."
            return
        }
        let demo = folder.lastPathComponent.localizedCaseInsensitiveContains("demo")
        let (name, appID) = Steam.games[demo ? 1 : 0]
        chosen = Game(name: name, appID: appID, folder: folder)
        if !games.contains(chosen!) { games.append(chosen!) }
        message = ""
    }

    func install() {
        guard let game = chosen else { return }
        if Installer.gameRunning(game.folder) { message = "Quit \(game.name) first."; return }
        guard let payload = Installer.payload else { message = "The installer's files are missing."; return }
        manual = nil
        do {
            _ = try Installer.copyFiles(from: payload, to: game.folder)
        } catch {
            message = "Couldn't copy RegenerateFM into \(game.name): \(error.localizedDescription)"
            return
        }
        var text = "RegenerateFM \(Installer.version) is installed in \(game.name)."
        if !Steam.needsChange(game, remove: false) {
            text += " Its Steam launch option is already set: press Play in Steam."
        } else if Steam.running() {
            text += " To finish, quit Steam (Steam > Quit Steam) and click Install again, so the installer can set the game's launch option. Or set it yourself in Steam (\(game.name) > Properties > General > Launch Options):"
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
        if !rosetta { text += " FM26 runs under Rosetta 2 with RegenerateFM: click Install Rosetta first." }
        message = text
    }

    func uninstall() {
        guard let game = chosen else { return }
        if Installer.gameRunning(game.folder) { message = "Quit \(game.name) first."; return }
        manual = nil
        // Without the launcher, Steam couldn't start the game with the option still set.
        if Steam.needsChange(game, remove: true) {
            if Steam.running() {
                message = "Quit Steam first (Steam > Quit Steam), so the installer can take RegenerateFM out of the game's launch option, then click Uninstall again."
                return
            }
            do { _ = try Steam.update(game, remove: true) } catch {
                message = "Couldn't change Steam's launch option (\(error.localizedDescription)). Remove \(Steam.manualText(game)) from Launch Options in Steam, then click Uninstall again."
                return
            }
        }
        do { message = try Installer.removeFiles(from: game.folder) } catch {
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
            Text("Adds RegenerateFM to Football Manager 26: Generate face and Update face in the game's menus. fm.app itself isn't changed.")
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Picker("Game:", selection: $model.chosen) {
                    ForEach(model.games, id: \.self) { game in
                        Text(game.name + (Installer.isInstalled(game.folder) ? " (installed)" : "")).tag(Optional(game))
                    }
                }
                .disabled(model.games.isEmpty)
                Button("Choose folder…") { model.choose() }
            }
            if let game = model.chosen {
                Text(game.folder.path).font(.caption).foregroundColor(.secondary).textSelection(.enabled)
            }
            HStack {
                Button(model.chosen.map { Installer.isInstalled($0.folder) } == true ? "Update" : "Install") { model.install() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.chosen == nil || model.working)
                Button("Uninstall") { model.uninstall() }
                    .disabled(model.chosen.map { !Installer.isInstalled($0.folder) } ?? true || model.working)
                if !model.rosetta {
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
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: folder)
            // Only a test Steam folder's launch options are changed from the command line.
            if Steam.testRoot != nil { print("launch options changed \(try Steam.update(game, remove: false))") }
        case "uninstall" where arguments.count == 2:
            let folder = URL(fileURLWithPath: arguments[1])
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: folder)
            if Steam.testRoot != nil { print("launch options changed \(try Steam.update(game, remove: true))") }
            print(try Installer.removeFiles(from: folder))
        case "needs-change" where arguments.count >= 2:
            let game = Game(name: Steam.games[0].0, appID: Steam.games[0].1, folder: URL(fileURLWithPath: arguments[1]))
            print(Steam.needsChange(game, remove: arguments.contains("--remove")))
        case "games":
            for game in Steam.installedGames() { print("\(game.appID) \(game.folder.path)") }
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
