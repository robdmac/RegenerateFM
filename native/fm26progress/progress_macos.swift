// SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md).
// regeneratefm-progress --pid PID --log LOG --name USER_DATA_NAME
// regeneratefm-progress --demo LANGUAGE FRACTION STAGE
//
// FM26's progress window on the Mac while BepInEx prepares the game:
// run_regeneratefm.sh starts it before FM26 (PID is the launcher's, which
// becomes FM26 or waits for it). It reads BepInEx/LogOutput.log (LOG) as
// BepInEx writes it, and shows the window only if BepInEx downloads Unity's
// libraries or makes FM26's interop assemblies; on any other start it exits
// once BepInEx has started its chainloader. It closes when RegenerateFM's
// plugin has loaded, when FM26 exits, or if BepInEx reports it failed. The
// text is in FM26's language, from current_language.xml in its user-data
// folder (USER_DATA_NAME, "Football Manager 26" or "... Demo"). --demo shows
// the window at a fixed point, for checking its layout. progress_windows.c is
// the same window on Windows, with the same milestones.

import AppKit

enum Text: Int { case heading, note, download, read, build, start }

// BepInEx's log lines in the order a first start writes them, with how far
// along each is and the stage it belongs to.
let milestones: [(text: String, done: Double, stage: Text)] = [
    ("Downloading unity base libraries", 0.02, .download),
    ("Detected outdated interop assemblies", 0.08, .read),
    ("Extracting unity base libraries", 0.08, .read),
    ("Running Cpp2IL", 0.10, .read),
    ("Cpp2IL finished", 0.38, .build),
    ("Generating interop assemblies", 0.40, .build),
    ("Writing assemblies...", 0.70, .build),
    ("Writing method pointer map", 0.88, .build),
    ("Il2CppInteropGen] Done!", 0.90, .build),
    ("Chainloader initialized", 0.93, .start),
]
// "Done in" lines between "Generating interop assemblies" and "Writing
// assemblies...": the generator's steps, which move the bar from 0.40 to 0.70.
let generatorSteps = 28.0

/// FM26's interface language from its preferences, lower case, or "".
func readLanguage(_ userData: String) -> String {
    let preferences = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/Sports Interactive/\(userData)/Preferences")
    let versions = (try? FileManager.default.contentsOfDirectory(at: preferences, includingPropertiesForKeys: nil)) ?? []
    var newest: (Date, String)?
    for version in versions where version.lastPathComponent.hasPrefix("version ") {
        let file = version.appendingPathComponent("current_language.xml")
        guard let text = try? String(contentsOf: file, encoding: .utf8),
              let written = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.modificationDate] as? Date,
              let start = text.range(of: "value=\""), let end = text[start.upperBound...].firstIndex(of: "\"") else { continue }
        if newest == nil || written > newest!.0 { newest = (written, String(text[start.upperBound..<end]).lowercased()) }
    }
    return newest?.1 ?? ""
}

final class Progress: NSObject {
    let texts: [String]
    let rightToLeft: Bool
    let log: String?
    let pid: pid_t
    let started = Date()
    var panel: NSPanel!
    var bar: NSProgressIndicator!
    var stageLabel: NSTextField!
    var shown = false
    var shownFraction = 0.0
    var lastDone = -1.0
    var milestoneAt = Date()
    var closingAt: Date?

    init(language: String, log: String?, pid: pid_t, gameName: String) {
        texts = Progress.texts(for: language).map { $0.replacingOccurrences(of: "FM26", with: gameName) }
        rightToLeft = (language.components(separatedBy: " (").first ?? language) == "arabic"
        self.log = log
        self.pid = pid
        super.init()
        makePanel()
    }

    /// The texts for FM's language name: its own, else its base language's, else English.
    static func texts(for language: String) -> [String] {
        let base = language.components(separatedBy: " (").first ?? language
        return translatedTexts[language] ?? translatedTexts[base] ?? englishTexts
    }

    func label(_ text: String, size: CGFloat, weight: NSFont.Weight) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: text)
        field.font = .systemFont(ofSize: size, weight: weight)
        field.alignment = rightToLeft ? .right : .left
        field.baseWritingDirection = rightToLeft ? .rightToLeft : .leftToRight
        field.preferredMaxLayoutWidth = 440
        return field
    }

    func makePanel() {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 480, height: 170),
                        styleMask: [.titled, .closable, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.title = "RegenerateFM"
        panel.level = .floating
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        let heading = label(texts[Text.heading.rawValue], size: 15, weight: .semibold)
        let note = label(texts[Text.note.rawValue], size: 12, weight: .regular)
        note.textColor = .secondaryLabelColor
        bar = NSProgressIndicator()
        bar.isIndeterminate = false
        bar.minValue = 0
        bar.maxValue = 1
        bar.style = .bar
        bar.userInterfaceLayoutDirection = rightToLeft ? .rightToLeft : .leftToRight
        stageLabel = label("", size: 12, weight: .regular)
        let stack = NSStackView(views: [heading, note, bar, stageLabel])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 10
        stack.setCustomSpacing(16, after: note)
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 20, bottom: 20, right: 20)
        stack.userInterfaceLayoutDirection = rightToLeft ? .rightToLeft : .leftToRight
        // Every row is as wide as the window inside its margins, so text starts
        // at the leading edge in either direction.
        for view in stack.arrangedSubviews {
            view.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -40).isActive = true
        }
        stack.widthAnchor.constraint(equalToConstant: 480).isActive = true
        panel.contentView = stack
        panel.contentView?.userInterfaceLayoutDirection = stack.userInterfaceLayoutDirection
        stack.layoutSubtreeIfNeeded()
        panel.setContentSize(stack.fittingSize)
        panel.center()
        panel.delegate = self
    }

    func show() {
        guard !shown else { return }
        shown = true
        // A non-activating panel can be key, so it looks active, without
        // taking the keyboard from FM26.
        panel.orderFrontRegardless()
        panel.makeKey()
    }

    func setProgress(_ fraction: Double, _ stage: Text) {
        if fraction > shownFraction {
            shownFraction = fraction
            bar.doubleValue = fraction
        }
        let text = texts[stage.rawValue]
        if stageLabel.stringValue != text { stageLabel.stringValue = text }
    }

    /// The log if BepInEx has written it since this started, else nil.
    func readLog() -> String? {
        guard let log = log, let written = (try? FileManager.default.attributesOfItem(atPath: log))?[.modificationDate] as? Date,
              written >= started, let data = FileManager.default.contents(atPath: log) else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    func gameRunning() -> Bool { pid <= 0 || kill(pid, 0) == 0 || errno == EPERM }

    /// Reads the log and moves the bar, and decides whether to show, close or
    /// keep watching. Between milestones the bar creeps towards the next one.
    func check() {
        let now = Date()
        if let closing = closingAt {
            if now.timeIntervalSince(closing) > 2.5 { NSApp.terminate(nil) }
            return
        }
        if !gameRunning() { NSApp.terminate(nil) }
        guard let text = readLog() else {
            if now.timeIntervalSince(started) > 180 { NSApp.terminate(nil) }
            return
        }
        if text.contains("Failed to download Unity base libraries") || text.contains("Failed to generate Il2Cpp interop") {
            NSApp.terminate(nil)
        }
        var preparing = false
        var done = 0.0, next = milestones[0].done
        var stage = Text.download
        for (index, milestone) in milestones.enumerated() where text.contains(milestone.text) {
            if milestone.done >= done {
                done = milestone.done
                stage = milestone.stage
                next = index + 1 < milestones.count ? milestones[index + 1].done : 1
            }
            if milestone.stage != .start { preparing = true }
        }
        if let generating = text.range(of: "Generating interop assemblies"), !text.contains("Writing assemblies...") {
            let steps = Double(text[generating.lowerBound...].components(separatedBy: "Il2CppInteropGen] Done in").count - 1)
            done = 0.40 + 0.30 * min(steps, generatorSteps) / generatorSteps
            next = done + 0.30 / generatorSteps
        }
        if !preparing {
            // An ordinary start: nothing to show.
            if text.contains("Chainloader initialized") || now.timeIntervalSince(started) > 600 { NSApp.terminate(nil) }
            return
        }
        show()
        if text.contains("Chainloader startup complete") {
            setProgress(1, .start)
            closingAt = now
            return
        }
        if done != lastDone { lastDone = done; milestoneAt = now }
        let seconds = now.timeIntervalSince(milestoneAt)
        setProgress(done + (next - done) * 0.85 * (1 - exp(-seconds / 30)), stage)
        if now.timeIntervalSince(started) > 3600 { NSApp.terminate(nil) }
    }
}

extension Progress: NSWindowDelegate {
    func windowWillClose(_ notification: Notification) { NSApp.terminate(nil) }
}

@main
enum Main {
    static func main() {
        var arguments = Array(CommandLine.arguments.dropFirst())
        var pid: pid_t = 0
        var log: String?
        var userData = "Football Manager 26"
        var demo: (language: String, fraction: Double, stage: Int)?
        while !arguments.isEmpty {
            let option = arguments.removeFirst()
            switch option {
            case "--pid" where !arguments.isEmpty: pid = pid_t(arguments.removeFirst()) ?? 0
            case "--log" where !arguments.isEmpty: log = arguments.removeFirst()
            case "--name" where !arguments.isEmpty: userData = arguments.removeFirst()
            case "--demo" where arguments.count >= 3:
                demo = (arguments.removeFirst(), Double(arguments.removeFirst()) ?? 0, Int(arguments.removeFirst()) ?? 2)
            default: break
            }
        }
        if demo == nil && log == nil { exit(2) }

        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        // "Football Manager 27" is FM27; FM26 and its Demo keep FM26's name.
        let gameName = userData.hasPrefix("Football Manager 27") ? "FM27" : "FM26"
        let progress = Progress(language: demo?.language ?? readLanguage(userData), log: log, pid: pid, gameName: gameName)
        if let demo = demo {
            progress.show()
            progress.setProgress(demo.fraction, Text(rawValue: Text.download.rawValue + demo.stage) ?? .build)
        } else {
            Timer.scheduledTimer(withTimeInterval: 0.3, repeats: true) { _ in progress.check() }
        }
        app.run()
    }
}
