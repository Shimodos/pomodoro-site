import AppKit
import AVFoundation

// clip.swift <frames-dir> <out.mp4>
// Composes the macOS promo clip (1080 × 1350, 30 fps, ~12 s, no sound) from real window
// captures of the Mac build: focus 00:05 → 00:01, the break auto-starts in blue, a drawn
// cursor switches the window to compact mode, end card. Frames expected in <frames-dir>:
//   f5 f4 f3 f2 f1 (focus, per second), b0 b1 b2 (break), c0 c1 c2 (compact) as .png (2x captures).
let args = CommandLine.arguments
guard args.count == 3 else { print("usage: clip.swift <frames-dir> <out.mp4>"); exit(2) }
let dir = args[1], outPath = args[2]

let W = 1080, H = 1350, fps: Int32 = 30
let duration: Double = 12.5

let focusFrames = ["f5", "f4", "f3", "f2", "f1"].map { loadImage("\(dir)/\($0).png") }
let breakFrames = ["b0", "b1", "b2"].map { loadImage("\(dir)/\($0).png") }
let compactFrames = ["c0", "c1", "c2"].map { loadImage("\(dir)/\($0).png") }

// Layout: the main window (340 × 540 pt capture at 2x) drawn 760 px wide, top-left anchored so
// compact mode (250 × 300) keeps the same top-left corner like the real app.
let mainWidth: CGFloat = 690
let topLeft = NSPoint(x: (CGFloat(W) - mainWidth) / 2, y: CGFloat(H) - 150)   // y from the bottom

func windowRect(_ image: NSImage, scale: CGFloat) -> NSRect {
    let px = pixelSize(image)
    let w = px.width / 2 * (mainWidth / 340) * scale
    let h = px.height / 2 * (mainWidth / 340) * scale
    // Zoom around the window's own top-left so the corner stays put.
    return NSRect(x: topLeft.x, y: topLeft.y - h, width: w, height: h)
}

/// Position of the compact button in a 2x main capture (pixels): x ≈ 565, y ≈ 44 from the top.
func compactButtonPoint(in rect: NSRect) -> NSPoint {
    NSPoint(x: rect.minX + rect.width * (565.0 / 680.0), y: rect.maxY - rect.height * (44.0 / 1080.0))
}

func ease(_ t: Double) -> Double { t < 0.5 ? 2 * t * t : 1 - pow(-2 * t + 2, 2) / 2 }
func clamp01(_ t: Double) -> Double { min(max(t, 0), 1) }

func drawFrame(time t: Double, in rect: NSRect) {
    drawBackdrop(in: rect)
    let zoom = CGFloat(1.0 + 0.04 * clamp01(t / duration))

    // Which window image, with crossfades at the two transitions.
    var current: NSImage
    var previous: NSImage? = nil
    var mix: CGFloat = 1
    switch t {
    case ..<5.0:
        current = focusFrames[min(Int(t), 4)]
    case ..<8.0:
        current = breakFrames[min(Int(t - 5.0), 2)]
        if t < 5.2 { previous = focusFrames[4]; mix = CGFloat(clamp01((t - 5.0) / 0.2)) }
    default:
        current = compactFrames[min(Int(t - 8.0), 2)]
    }

    let rect = windowRect(current, scale: zoom)
    if let previous {
        drawWindow(previous, in: windowRect(previous, scale: zoom), alpha: 1 - mix)
        drawWindow(current, in: rect, alpha: mix)
    } else {
        drawWindow(current, in: rect)
    }

    // Cursor: appears at 6.4 s, glides to the compact button, clicks at 7.9 s, drifts away.
    if t >= 6.4 {
        let target = compactButtonPoint(in: windowRect(breakFrames[2], scale: zoom))
        let start = NSPoint(x: rect.maxX + 60, y: rect.minY + 80)
        let p = ease(clamp01((t - 6.4) / 1.3))
        var pos = NSPoint(x: start.x + (target.x - start.x) * p, y: start.y + (target.y - start.y) * p)
        if t > 8.1 {
            let q = ease(clamp01((t - 8.1) / 1.2))
            pos = NSPoint(x: target.x + 140 * q, y: target.y - 160 * q)
        }
        let pressing = t >= 7.85 && t < 8.05
        drawCursor(at: pos, scale: pressing ? 1.6 : 1.9)
    }

    // End card.
    if t >= 10.2 {
        let a = CGFloat(clamp01((t - 10.2) / 0.6))
        drawText("Pomodoro · now on macOS", font: serifFont(60), color: ink.withAlphaComponent(a), at: NSPoint(x: 0, y: 150), alignment: .center, width: rect.width > 0 ? CGFloat(W) : CGFloat(W))
        drawText("Free and open source · shimodos.github.io/pomodoro-site", font: NSFont.systemFont(ofSize: 26), color: inkMuted.withAlphaComponent(a), at: NSPoint(x: 0, y: 100), alignment: .center, width: CGFloat(W))
    }
}

// MARK: - Encode with AVAssetWriter

let url = URL(fileURLWithPath: outPath)
try? FileManager.default.removeItem(at: url)
let writer = try! AVAssetWriter(outputURL: url, fileType: .mp4)
let settings: [String: Any] = [
    AVVideoCodecKey: AVVideoCodecType.h264,
    AVVideoWidthKey: W,
    AVVideoHeightKey: H,
    AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 6_000_000, AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel],
]
let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
input.expectsMediaDataInRealTime = false
let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
    kCVPixelBufferWidthKey as String: W,
    kCVPixelBufferHeightKey as String: H,
])
writer.add(input)
writer.startWriting()
writer.startSession(atSourceTime: .zero)

let total = Int(duration * Double(fps))
for i in 0..<total {
    while !input.isReadyForMoreMediaData { Thread.sleep(forTimeInterval: 0.005) }
    var buffer: CVPixelBuffer?
    CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
    guard let pb = buffer else { fatalError("no pixel buffer") }
    CVPixelBufferLockBaseAddress(pb, [])
    let ctx = CGContext(data: CVPixelBufferGetBaseAddress(pb), width: W, height: H, bitsPerComponent: 8,
                        bytesPerRow: CVPixelBufferGetBytesPerRow(pb), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
    drawFrame(time: Double(i) / Double(fps), in: NSRect(x: 0, y: 0, width: W, height: H))
    NSGraphicsContext.restoreGraphicsState()
    CVPixelBufferUnlockBaseAddress(pb, [])
    adaptor.append(pb, withPresentationTime: CMTime(value: CMTimeValue(i), timescale: fps))
    if i % 60 == 0 { print("frame \(i)/\(total)") }
}
input.markAsFinished()
let done = DispatchSemaphore(value: 0)
writer.finishWriting { done.signal() }
done.wait()
if writer.status != .completed { print("writer failed: \(String(describing: writer.error))"); exit(1) }
print("wrote \(outPath)")
