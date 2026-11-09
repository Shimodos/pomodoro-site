import AppKit

// Shared drawing helpers for the macOS promo renderers (stills and the clip).

let focusColor = NSColor(srgbRed: 1.0, green: 0.42, blue: 0.42, alpha: 1)      // #FF6B6B
let breakColor = NSColor(srgbRed: 0.22, green: 0.74, blue: 0.97, alpha: 1)     // #38BDF8
let paper = NSColor(srgbRed: 0.97, green: 0.97, blue: 0.98, alpha: 1)          // #F7F7FA
let ink = NSColor(srgbRed: 0.106, green: 0.106, blue: 0.125, alpha: 1)         // #1B1B20
let inkMuted = NSColor(srgbRed: 0.43, green: 0.43, blue: 0.48, alpha: 1)       // #6E6E7A

func loadImage(_ path: String) -> NSImage {
    guard let img = NSImage(contentsOfFile: path) else {
        FileHandle.standardError.write("cannot load \(path)\n".data(using: .utf8)!)
        exit(1)
    }
    return img
}

/// Pixel size of a PNG (screenshots are 2x, NSImage.size reports points).
func pixelSize(_ image: NSImage) -> NSSize {
    guard let rep = image.representations.first else { return image.size }
    return NSSize(width: rep.pixelsWide, height: rep.pixelsHigh)
}

/// Paper backdrop with two soft color washes, like the website hero.
func drawBackdrop(in rect: NSRect, dark: Bool = false) {
    (dark ? NSColor(srgbRed: 0.06, green: 0.066, blue: 0.082, alpha: 1) : paper).setFill()
    rect.fill()
    let washes: [(NSColor, NSPoint, CGFloat)] = [
        (focusColor.withAlphaComponent(dark ? 0.16 : 0.22), NSPoint(x: rect.maxX - rect.width * 0.1, y: rect.maxY - rect.height * 0.05), rect.width * 0.55),
        (breakColor.withAlphaComponent(dark ? 0.16 : 0.22), NSPoint(x: rect.minX + rect.width * 0.05, y: rect.minY + rect.height * 0.15), rect.width * 0.5),
    ]
    for (color, center, radius) in washes {
        let gradient = NSGradient(colorsAndLocations: (color, 0), (color.withAlphaComponent(0), 1))!
        gradient.draw(fromCenter: center, radius: 0, toCenter: center, radius: radius, options: [.drawsAfterEndingLocation])
    }
}

/// Draws a window screenshot with a soft shadow. `frame` is where the image goes (points of the canvas).
func drawWindow(_ image: NSImage, in frame: NSRect, alpha: CGFloat = 1, shadow: CGFloat = 60) {
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowColor = NSColor.black.withAlphaComponent(0.28 * alpha)
    sh.shadowBlurRadius = shadow
    sh.shadowOffset = NSSize(width: 0, height: -shadow * 0.45)
    sh.set()
    image.draw(in: frame, from: .zero, operation: .sourceOver, fraction: alpha, respectFlipped: false, hints: [.interpolation: NSImageInterpolation.high])
    NSGraphicsContext.restoreGraphicsState()
}

func serifFont(_ size: CGFloat, weight: NSFont.Weight = .semibold) -> NSFont {
    let base = NSFont.systemFont(ofSize: size, weight: weight)
    return base.fontDescriptor.withDesign(.serif).flatMap { NSFont(descriptor: $0, size: size) } ?? base
}

func drawText(_ text: String, font: NSFont, color: NSColor, at origin: NSPoint, alignment: NSTextAlignment = .left, width: CGFloat? = nil) {
    let p = NSMutableParagraphStyle()
    p.alignment = alignment
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: p]
    let s = NSAttributedString(string: text, attributes: attrs)
    if let width {
        s.draw(in: NSRect(x: origin.x, y: origin.y, width: width, height: s.size().height + 4))
    } else {
        s.draw(at: origin)
    }
}

/// Renders `draw` into a PNG of `pixels` size at 1x scale (points == pixels).
func renderPNG(pixels: NSSize, to path: String, _ draw: (NSRect) -> Void) {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(pixels.width), pixelsHigh: Int(pixels.height),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw(NSRect(origin: .zero, size: pixels))
    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
    print("wrote \(path)")
}

/// Arrow cursor, macOS style, tip at `tip`.
func drawCursor(at tip: NSPoint, scale: CGFloat = 1) {
    let path = NSBezierPath()
    let pts: [(CGFloat, CGFloat)] = [(0, 0), (0, -18), (4.5, -14), (8, -21), (11, -19.5), (7.5, -12.5), (13, -12.5)]
    for (i, p) in pts.enumerated() {
        let pt = NSPoint(x: tip.x + p.0 * scale, y: tip.y + p.1 * scale)
        if i == 0 { path.move(to: pt) } else { path.line(to: pt) }
    }
    path.close()
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowColor = NSColor.black.withAlphaComponent(0.35)
    sh.shadowBlurRadius = 4 * scale
    sh.shadowOffset = NSSize(width: 0, height: -1.5 * scale)
    sh.set()
    NSColor.black.setFill()
    path.fill()
    NSGraphicsContext.restoreGraphicsState()
    NSColor.white.setStroke()
    path.lineWidth = 1.4 * scale
    path.lineJoinStyle = .round
    path.stroke()
}
