import AppKit

// stills.swift <shots-dir> <out-dir>
// Composes the announcement images (1600 × 900) from real captures of the Mac build:
//   1-hero.png      "Now on macOS": focus running (light) with the compact break window (dark)
//   2-colors.png    break running (dark) next to the Break tab viewed while focus runs (light)
//   3-settings.png  the settings window (dark)
//   4-installer.png the DMG installer window
let args = CommandLine.arguments
guard args.count == 3 else { print("usage: stills.swift <shots-dir> <out-dir>"); exit(2) }
let shots = args[1], out = args[2]
try? FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)

let canvas = NSSize(width: 1600, height: 900)

/// Fits `image` (2x pixels) to a frame `height` points tall, anchored at `origin` (bottom-left).
func frame(for image: NSImage, height: CGFloat, x: CGFloat, y: CGFloat) -> NSRect {
    let px = pixelSize(image)
    let w = height * px.width / px.height
    return NSRect(x: x, y: y, width: w, height: height)
}

// 1 — hero
let focus = loadImage("\(shots)/focus-running-light.png")
let compact = loadImage("\(shots)/compact-break-dark.png")
renderPNG(pixels: canvas, to: "\(out)/1-hero.png") { rect in
    drawBackdrop(in: rect)
    drawText("Now on macOS", font: serifFont(84), color: ink, at: NSPoint(x: 110, y: rect.height - 250))
    drawText("The same native Pomodoro timer. Swift, AppKit and SwiftUI.", font: NSFont.systemFont(ofSize: 30), color: inkMuted, at: NSPoint(x: 112, y: rect.height - 310))
    drawText("Menu bar item · notifications · Open at login · one settings.json with Windows", font: NSFont.systemFont(ofSize: 24), color: inkMuted, at: NSPoint(x: 112, y: rect.height - 360))
    drawText("shimodos.github.io/pomodoro-site", font: NSFont.systemFont(ofSize: 24, weight: .semibold), color: ink, at: NSPoint(x: 112, y: 120))
    let main = frame(for: focus, height: 760, x: 960, y: 70)
    drawWindow(focus, in: main)
    let small = frame(for: compact, height: 330, x: 780, y: 40)
    drawWindow(compact, in: small, shadow: 40)
}

// 2 — colors
let breakDark = loadImage("\(shots)/break-running-dark.png")
let breakView = loadImage("\(shots)/break-view-light.png")
renderPNG(pixels: canvas, to: "\(out)/2-colors.png") { rect in
    drawBackdrop(in: rect)
    drawText("Focus is red, breaks are blue. Tabs only look.", font: serifFont(52), color: ink, at: NSPoint(x: 0, y: rect.height - 120), alignment: .center, width: rect.width)
    let left = frame(for: breakDark, height: 700, x: 330, y: 50)
    let right = frame(for: breakView, height: 700, x: 830, y: 50)
    drawWindow(breakDark, in: left)
    drawWindow(breakView, in: right)
}

// 3 — settings
let settings = loadImage("\(shots)/settings-timer-dark-win-Settings.png")
renderPNG(pixels: canvas, to: "\(out)/3-settings.png") { rect in
    drawBackdrop(in: rect, dark: true)
    drawText("Settings: timer, sound, appearance, system", font: serifFont(44), color: .white, at: NSPoint(x: 0, y: rect.height - 100), alignment: .center, width: rect.width)
    let f = frame(for: settings, height: 720, x: 0, y: 50)
    drawWindow(settings, in: NSRect(x: (rect.width - f.width) / 2, y: 50, width: f.width, height: f.height))
}

// 4 — installer
let dmg = loadImage("\(shots)/dmg-installer.png")
renderPNG(pixels: canvas, to: "\(out)/4-installer.png") { rect in
    drawBackdrop(in: rect)
    drawText("Download the .dmg, drag to Applications", font: serifFont(44), color: ink, at: NSPoint(x: 0, y: rect.height - 100), alignment: .center, width: rect.width)
    let f = frame(for: dmg, height: 640, x: 0, y: 110)
    drawWindow(dmg, in: NSRect(x: (rect.width - f.width) / 2, y: 110, width: f.width, height: f.height))
}
