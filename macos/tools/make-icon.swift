import AppKit
import Foundation

// Draws the app icon (MACOS-17) and writes an .iconset for iconutil. Run it through make-icon.sh.
//
// The icon is the app in one glance: a quiet room at night, and something listening in it. A deep
// night-blue squircle, a soft warm glow (the nightlight), and the waveform that is also the menu
// bar's live icon — so the thing in the Dock and the thing in the menu bar are recognisably the
// same app.
//
// Apple's grid: the art is 1024×1024, and the rounded square occupies the middle ~824×824 with a
// corner radius of ~185. Anything drawn edge-to-edge looks oversized next to every other icon in
// the Dock, which is the tell of an app that did not read the guidelines.

let outputDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "./AppIcon.iconset"
try? FileManager.default.createDirectory(atPath: outputDir, withIntermediateDirectories: true)

func drawIcon(size: CGFloat) -> NSBitmapImageRep {
    let pixels = Int(size)
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: pixels,
        pixelsHigh: pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    )!
    rep.size = NSSize(width: size, height: size)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let context = NSGraphicsContext.current!.cgContext

    let unit = size / 1024.0
    let inset = 100.0 * unit
    let body = CGRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
    let radius = 185.0 * unit

    // The squircle, with the light coming from above as it does on every other icon on the shelf.
    let shape = CGPath(roundedRect: body, cornerWidth: radius, cornerHeight: radius, transform: nil)
    context.saveGState()
    context.setShadow(
        offset: CGSize(width: 0, height: -10 * unit),
        blur: 24 * unit,
        color: NSColor.black.withAlphaComponent(0.35).cgColor
    )
    context.addPath(shape)
    context.setFillColor(NSColor.black.cgColor)
    context.fillPath()
    context.restoreGState()

    context.saveGState()
    context.addPath(shape)
    context.clip()

    let space = CGColorSpaceCreateDeviceRGB()
    let night = CGGradient(
        colorsSpace: space,
        colors: [
            NSColor(calibratedRed: 0.24, green: 0.29, blue: 0.60, alpha: 1).cgColor,
            NSColor(calibratedRed: 0.05, green: 0.06, blue: 0.14, alpha: 1).cgColor,
        ] as CFArray,
        locations: [0, 1]
    )!
    context.drawLinearGradient(
        night,
        start: CGPoint(x: body.midX, y: body.maxY),
        end: CGPoint(x: body.midX, y: body.minY),
        options: []
    )

    // The nightlight: a warm glow low in the room, the one thing that is not blue.
    let glow = CGGradient(
        colorsSpace: space,
        colors: [
            NSColor(calibratedRed: 1.0, green: 0.78, blue: 0.48, alpha: 0.50).cgColor,
            NSColor(calibratedRed: 1.0, green: 0.72, blue: 0.45, alpha: 0.0).cgColor,
        ] as CFArray,
        locations: [0, 1]
    )!
    context.drawRadialGradient(
        glow,
        startCenter: CGPoint(x: body.midX, y: body.minY + body.height * 0.18),
        startRadius: 0,
        endCenter: CGPoint(x: body.midX, y: body.minY + body.height * 0.18),
        endRadius: body.width * 0.62,
        options: []
    )

    // The waveform — the same shape the menu bar shows while the feed is live. Five rounded bars,
    // symmetric, tallest in the middle: sound in a quiet room.
    let heights: [CGFloat] = [0.26, 0.52, 0.82, 0.52, 0.26]
    let barWidth = body.width * 0.072
    let gap = body.width * 0.055
    let totalWidth = barWidth * CGFloat(heights.count) + gap * CGFloat(heights.count - 1)
    var x = body.midX - totalWidth / 2

    for fraction in heights {
        let height = body.height * 0.62 * fraction
        let bar = CGRect(x: x, y: body.midY - height / 2, width: barWidth, height: height)
        let path = CGPath(
            roundedRect: bar,
            cornerWidth: barWidth / 2,
            cornerHeight: barWidth / 2,
            transform: nil
        )
        context.saveGState()
        context.setShadow(
            offset: .zero,
            blur: 22 * unit,
            color: NSColor(calibratedRed: 0.75, green: 0.85, blue: 1.0, alpha: 0.55).cgColor
        )
        context.addPath(path)
        context.setFillColor(NSColor.white.withAlphaComponent(0.95).cgColor)
        context.fillPath()
        context.restoreGState()
        x += barWidth + gap
    }

    // The glass highlight across the top — subtle, and the reason it reads as a physical object.
    let sheen = CGGradient(
        colorsSpace: space,
        colors: [
            NSColor.white.withAlphaComponent(0.20).cgColor,
            NSColor.white.withAlphaComponent(0.0).cgColor,
        ] as CFArray,
        locations: [0, 1]
    )!
    context.drawLinearGradient(
        sheen,
        start: CGPoint(x: body.midX, y: body.maxY),
        end: CGPoint(x: body.midX, y: body.midY + body.height * 0.08),
        options: []
    )

    context.restoreGState()

    // The hairline that keeps the squircle's edge crisp at 16pt.
    context.addPath(shape)
    context.setStrokeColor(NSColor.white.withAlphaComponent(0.12).cgColor)
    context.setLineWidth(2 * unit)
    context.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep
}

// The sizes iconutil expects, and no others.
let variants: [(name: String, size: CGFloat)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]

for variant in variants {
    let rep = drawIcon(size: variant.size)
    guard let data = rep.representation(using: .png, properties: [:]) else {
        FileHandle.standardError.write("could not encode \(variant.name)\n".data(using: .utf8)!)
        exit(1)
    }
    let url = URL(fileURLWithPath: outputDir).appendingPathComponent("\(variant.name).png")
    try data.write(to: url)
}

print("wrote \(variants.count) images to \(outputDir)")
