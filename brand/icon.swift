import AppKit
import Foundation

// THE APP ICON. One mark, every platform, now and for whatever ships next.
//
// It is generated rather than drawn by hand, and it is generated from the numbers in `Brand` below,
// because an icon that is exported once per platform is an icon that quietly stops matching itself.
// The Mac's .icns and Android's adaptive icon are two *renderings* of the same description here —
// change a colour or a bar and both move together, or neither does.
//
// The mark: a quiet room at night, and something listening in it. A deep night-blue field, a warm
// nightlight low in the frame, and the waveform that is also the Mac's menu bar icon while the feed
// is live — so the thing in the Dock, the thing in the launcher and the thing in the menu bar are
// recognisably the same app.
//
// Adding a platform means adding a renderer at the bottom. It does not mean redrawing the icon.
//
//   swift brand/icon.swift --macos <iconset-dir> --android <res-dir> --ios <xcassets-dir> [--preview <png>]

// MARK: - The mark itself

enum Brand {
    /// The night. Top of the field to the bottom of it.
    static let skyTop = (r: 0.24, g: 0.29, b: 0.60)
    static let skyBottom = (r: 0.05, g: 0.06, b: 0.14)

    /// The nightlight: the one warm thing in a blue picture, low down, where a lamp would be.
    static let glow = (r: 1.0, g: 0.78, b: 0.48, a: 0.50)
    static let glowCentreY: Double = 0.18 // up from the bottom of the field
    static let glowRadius: Double = 0.62 // of the field's width

    /// The waveform. Five rounded bars, symmetric, tallest in the middle: sound in a quiet room.
    /// All fractions are of the *field* — the visible body of the icon, whatever each platform's
    /// mask turns that into.
    static let barHeights: [Double] = [0.26, 0.52, 0.82, 0.52, 0.26]
    static let barWidth: Double = 0.072
    static let barGap: Double = 0.055
    static let barSpan: Double = 0.62 // the tallest bar, as a fraction of the field's height

    static func hex(_ c: (r: Double, g: Double, b: Double), alpha: Double = 1) -> String {
        let a = Int((alpha * 255).rounded())
        let r = Int((c.r * 255).rounded())
        let g = Int((c.g * 255).rounded())
        let b = Int((c.b * 255).rounded())
        return String(format: "#%02X%02X%02X%02X", a, r, g, b)
    }
}

// MARK: - Drawing it (CoreGraphics: the Mac's .icns, and the preview)

/// Two rects, not one, and the difference matters.
///
/// `sky` is where the night is painted — on a Mac that is the squircle; on Android it is the whole
/// 108dp canvas, because the launcher's mask crops it and the paint has to reach past the crop.
/// `mark` is the *field*: the area the waveform is sized against, which is the part a mask actually
/// shows. Size the waveform against the canvas instead and Android's icon comes out with a mark
/// half again too big for its circle — which is exactly what the first preview showed.
func drawField(in context: CGContext, sky: CGRect, mark: CGRect, unit: CGFloat, clipTo shape: CGPath?) {
    let body = sky
    context.saveGState()
    if let shape {
        context.addPath(shape)
        context.clip()
    }

    let space = CGColorSpaceCreateDeviceRGB()
    let night = CGGradient(
        colorsSpace: space,
        colors: [
            NSColor(calibratedRed: Brand.skyTop.r, green: Brand.skyTop.g, blue: Brand.skyTop.b, alpha: 1).cgColor,
            NSColor(calibratedRed: Brand.skyBottom.r, green: Brand.skyBottom.g, blue: Brand.skyBottom.b, alpha: 1).cgColor,
        ] as CFArray,
        locations: [0, 1]
    )!
    context.drawLinearGradient(
        night,
        start: CGPoint(x: body.midX, y: body.maxY),
        end: CGPoint(x: body.midX, y: body.minY),
        options: []
    )

    let glow = CGGradient(
        colorsSpace: space,
        colors: [
            NSColor(calibratedRed: Brand.glow.r, green: Brand.glow.g, blue: Brand.glow.b, alpha: Brand.glow.a).cgColor,
            NSColor(calibratedRed: Brand.glow.r, green: Brand.glow.g, blue: Brand.glow.b, alpha: 0).cgColor,
        ] as CFArray,
        locations: [0, 1]
    )!
    let centre = CGPoint(x: body.midX, y: body.minY + body.height * CGFloat(Brand.glowCentreY))
    context.drawRadialGradient(
        glow,
        startCenter: centre,
        startRadius: 0,
        endCenter: centre,
        endRadius: body.width * CGFloat(Brand.glowRadius),
        options: []
    )

    drawWaveform(in: context, body: mark, unit: unit)

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
}

func drawWaveform(in context: CGContext, body: CGRect, unit: CGFloat) {
    let barWidth = body.width * CGFloat(Brand.barWidth)
    let gap = body.width * CGFloat(Brand.barGap)
    let total = barWidth * CGFloat(Brand.barHeights.count) + gap * CGFloat(Brand.barHeights.count - 1)
    var x = body.midX - total / 2

    for fraction in Brand.barHeights {
        let height = body.height * CGFloat(Brand.barSpan) * CGFloat(fraction)
        let bar = CGRect(x: x, y: body.midY - height / 2, width: barWidth, height: height)
        let path = CGPath(roundedRect: bar, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil)
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
}

// MARK: - macOS (DESK-17)

/// Apple's grid: the art is 1024×1024 and the rounded square occupies the middle ~824×824, corner
/// radius ~185. Anything drawn edge to edge looks oversized next to every other icon in the Dock,
/// which is the tell of an app that did not read the guidelines.
func drawMacIcon(size: CGFloat) -> NSBitmapImageRep {
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
    let shape = CGPath(roundedRect: body, cornerWidth: radius, cornerHeight: radius, transform: nil)

    // The drop shadow every Mac icon casts onto the Dock.
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

    // On a Mac the squircle is both the sky and the field: there is no launcher mask to survive.
    drawField(in: context, sky: body, mark: body, unit: unit, clipTo: shape)

    // The hairline that keeps the squircle's edge crisp at 16pt.
    context.addPath(shape)
    context.setStrokeColor(NSColor.white.withAlphaComponent(0.12).cgColor)
    context.setLineWidth(2 * unit)
    context.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep
}

func writeMacIconset(to directory: String) throws {
    try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
    let variants: [(name: String, size: CGFloat)] = [
        ("icon_16x16", 16), ("icon_16x16@2x", 32),
        ("icon_32x32", 32), ("icon_32x32@2x", 64),
        ("icon_128x128", 128), ("icon_128x128@2x", 256),
        ("icon_256x256", 256), ("icon_256x256@2x", 512),
        ("icon_512x512", 512), ("icon_512x512@2x", 1024),
    ]
    for variant in variants {
        guard let data = drawMacIcon(size: variant.size).representation(using: .png, properties: [:]) else {
            throw Failure("could not encode \(variant.name)")
        }
        try data.write(to: URL(fileURLWithPath: directory).appendingPathComponent("\(variant.name).png"))
    }
    print("macOS: \(variants.count) images → \(directory)")
}

// MARK: - iOS (IOS-1)

/// iOS masks the icon itself — a continuous rounded square — and wants the art **full-bleed and
/// opaque**: no baked corners, no padding, no shadow, no transparency (the Mac's floating squircle is
/// the opposite convention). So the night reaches every edge, and the waveform is sized against the
/// whole square, which lands it in the same proportion the Mac's squircle shows — the same mark (UI-3).
func drawIosIcon(size: CGFloat) -> NSBitmapImageRep {
    let pixels = Int(size)
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    rep.size = NSSize(width: size, height: size)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let context = NSGraphicsContext.current!.cgContext
    let body = CGRect(x: 0, y: 0, width: size, height: size)
    drawField(in: context, sky: body, mark: body, unit: size / 1024.0, clipTo: nil)
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

/// Writes an asset catalog with the single 1024×1024 icon Xcode 14+ takes, which `actool` compiles
/// into the app's `Assets.car` (see ios/build.sh).
func writeIosAppIcon(to xcassets: String) throws {
    let catalog = URL(fileURLWithPath: xcassets)
    let appicon = catalog.appendingPathComponent("AppIcon.appiconset")
    try FileManager.default.createDirectory(at: appicon, withIntermediateDirectories: true)

    let catalogInfo = "{\n  \"info\" : { \"author\" : \"xcode\", \"version\" : 1 }\n}\n"
    try catalogInfo.write(to: catalog.appendingPathComponent("Contents.json"), atomically: true, encoding: .utf8)

    guard let data = drawIosIcon(size: 1024).representation(using: .png, properties: [:]) else {
        throw Failure("could not encode the iOS icon")
    }
    try data.write(to: appicon.appendingPathComponent("icon-1024.png"))

    let contents = """
    {
      "images" : [
        {
          "filename" : "icon-1024.png",
          "idiom" : "universal",
          "platform" : "ios",
          "size" : "1024x1024"
        }
      ],
      "info" : { "author" : "xcode", "version" : 1 }
    }

    """
    try contents.write(to: appicon.appendingPathComponent("Contents.json"), atomically: true, encoding: .utf8)
    print("iOS: 1024 app icon → \(xcassets)")
}

// MARK: - Android

/// Android does not want a picture of an icon; it wants the *layers*, and applies its own mask —
/// a circle on one launcher, a squircle on the next. So the squircle is not baked in here: the
/// background bleeds to the edge of the 108dp canvas and the waveform is kept inside the 66dp safe
/// zone, which is the only part every mask is guaranteed to show.
///
/// The layers are vectors, not PNGs, so one file serves every density — and they are written from
/// the same numbers the Mac's bitmap is drawn from, which is the whole point of this file.
enum Android {
    static let canvas: Double = 108 // dp, the adaptive-icon canvas
    static let field: Double = 72 // dp, what a mask actually shows — the "body" of the mark

    static func vectorHeader(_ extra: String = "") -> String {
        """
        <vector xmlns:android="http://schemas.android.com/apk/res/android"
            xmlns:aapt="http://schemas.android.com/aapt"
            android:width="108dp"
            android:height="108dp"
            android:viewportWidth="108"
            android:viewportHeight="108">\(extra)
        """
    }

    /// The night and the nightlight, edge to edge (the mask crops it, whatever shape the mask is).
    static func background() -> String {
        let glowY = canvas - canvas * Brand.glowCentreY // vector y grows downward
        let glowR = canvas * Brand.glowRadius
        return """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- GENERATED by brand/icon.swift — do not edit. Run ./brand/build.sh. -->
        \(vectorHeader())
            <path android:pathData="M0,0h108v108h-108z">
                <aapt:attr name="android:fillColor">
                    <gradient
                        android:type="linear"
                        android:startX="54" android:startY="0"
                        android:endX="54" android:endY="108">
                        <item android:offset="0" android:color="\(Brand.hex(Brand.skyTop))" />
                        <item android:offset="1" android:color="\(Brand.hex(Brand.skyBottom))" />
                    </gradient>
                </aapt:attr>
            </path>
            <path android:pathData="M0,0h108v108h-108z">
                <aapt:attr name="android:fillColor">
                    <gradient
                        android:type="radial"
                        android:centerX="54" android:centerY="\(fmt(glowY))"
                        android:gradientRadius="\(fmt(glowR))">
                        <item android:offset="0" android:color="\(Brand.hex((Brand.glow.r, Brand.glow.g, Brand.glow.b), alpha: Brand.glow.a))" />
                        <item android:offset="1" android:color="\(Brand.hex((Brand.glow.r, Brand.glow.g, Brand.glow.b), alpha: 0))" />
                    </gradient>
                </aapt:attr>
            </path>
            <path android:pathData="M0,0h108v54h-108z">
                <aapt:attr name="android:fillColor">
                    <gradient
                        android:type="linear"
                        android:startX="54" android:startY="0"
                        android:endX="54" android:endY="54">
                        <item android:offset="0" android:color="#33FFFFFF" />
                        <item android:offset="1" android:color="#00FFFFFF" />
                    </gradient>
                </aapt:attr>
            </path>
        </vector>

        """
    }

    /// The waveform, in white, centred in the safe zone.
    static func foreground(color: String = "#F2FFFFFF") -> String {
        """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- GENERATED by brand/icon.swift — do not edit. Run ./brand/build.sh. -->
        \(vectorHeader())
        \(bars(color: color))
        </vector>

        """
    }

    private static func bars(color: String) -> String {
        let barWidth = field * Brand.barWidth
        let gap = field * Brand.barGap
        let total = barWidth * Double(Brand.barHeights.count) + gap * Double(Brand.barHeights.count - 1)
        var x = canvas / 2 - total / 2
        var paths: [String] = []

        for fraction in Brand.barHeights {
            let height = field * Brand.barSpan * fraction
            let y = canvas / 2 - height / 2
            let r = barWidth / 2
            paths.append("""
                <path
                    android:fillColor="\(color)"
                    android:pathData="M\(fmt(x)),\(fmt(y + r)) a\(fmt(r)),\(fmt(r)) 0 0,1 \(fmt(barWidth)),0 v\(fmt(height - barWidth)) a\(fmt(r)),\(fmt(r)) 0 0,1 -\(fmt(barWidth)),0 z" />
            """)
            x += barWidth + gap
        }
        return paths.joined(separator: "\n")
    }

    static func adaptiveIcon() -> String {
        """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- GENERATED by brand/icon.swift — do not edit. Run ./brand/build.sh. -->
        <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
            <background android:drawable="@drawable/ic_launcher_background" />
            <foreground android:drawable="@drawable/ic_launcher_foreground" />
            <!-- Android 13+ themed icons: the launcher tints this itself, so it is the mark alone. -->
            <monochrome android:drawable="@drawable/ic_launcher_monochrome" />
        </adaptive-icon>

        """
    }

    private static func fmt(_ value: Double) -> String {
        String(format: "%.2f", value)
    }
}

func writeAndroid(to res: String) throws {
    let drawable = URL(fileURLWithPath: res).appendingPathComponent("drawable")
    let mipmap = URL(fileURLWithPath: res).appendingPathComponent("mipmap-anydpi-v26")
    try FileManager.default.createDirectory(at: drawable, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: mipmap, withIntermediateDirectories: true)

    try Android.background()
        .write(to: drawable.appendingPathComponent("ic_launcher_background.xml"), atomically: true, encoding: .utf8)
    try Android.foreground()
        .write(to: drawable.appendingPathComponent("ic_launcher_foreground.xml"), atomically: true, encoding: .utf8)
    try Android.foreground(color: "#FFFFFFFF")
        .write(to: drawable.appendingPathComponent("ic_launcher_monochrome.xml"), atomically: true, encoding: .utf8)
    try Android.adaptiveIcon()
        .write(to: mipmap.appendingPathComponent("ic_launcher.xml"), atomically: true, encoding: .utf8)
    print("Android: 3 vectors + adaptive icon → \(res)")
}

// MARK: - A preview, so the thing can be looked at rather than imagined

/// Draws what each platform's mask will actually show, side by side: the Mac's squircle and the
/// circle Android launchers most often use.
func writePreview(to path: String) throws {
    let size: CGFloat = 512
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(size * 2), pixelsHigh: Int(size),
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let context = NSGraphicsContext.current!.cgContext

    context.setFillColor(NSColor(calibratedWhite: 0.55, alpha: 1).cgColor)
    context.fill(CGRect(x: 0, y: 0, width: size * 2, height: size))

    // Left: the Mac.
    let mac = drawMacIcon(size: size)
    mac.draw(in: CGRect(x: 0, y: 0, width: size, height: size))

    // Right: the Android layers, through a circular mask — the harshest crop any launcher applies.
    // Same arithmetic the vectors are written with: the sky is the whole 108dp canvas, the mark is
    // sized against the 72dp the mask shows.
    let unit = size / 1024
    let canvasRect = CGRect(x: size, y: 0, width: size, height: size)
    let fieldScale = CGFloat(Android.field / Android.canvas)
    let markRect = CGRect(
        x: canvasRect.midX - size * fieldScale / 2,
        y: canvasRect.midY - size * fieldScale / 2,
        width: size * fieldScale,
        height: size * fieldScale
    )
    context.saveGState()
    context.addEllipse(in: markRect) // the mask: 72dp of a 108dp canvas
    context.clip()
    drawField(in: context, sky: canvasRect, mark: markRect, unit: unit, clipTo: nil)
    context.restoreGState()

    NSGraphicsContext.restoreGraphicsState()
    guard let data = rep.representation(using: .png, properties: [:]) else { throw Failure("preview") }
    try data.write(to: URL(fileURLWithPath: path))
    print("preview → \(path)")
}

// MARK: - Entry

struct Failure: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

func fmt(_ value: Double) -> String { String(format: "%.2f", value) }

func argument(_ name: String) -> String? {
    guard let index = CommandLine.arguments.firstIndex(of: name),
          index + 1 < CommandLine.arguments.count
    else {
        return nil
    }
    return CommandLine.arguments[index + 1]
}

do {
    var did = false
    if let macos = argument("--macos") {
        try writeMacIconset(to: macos)
        did = true
    }
    if let android = argument("--android") {
        try writeAndroid(to: android)
        did = true
    }
    if let ios = argument("--ios") {
        try writeIosAppIcon(to: ios)
        did = true
    }
    if let preview = argument("--preview") {
        try writePreview(to: preview)
        did = true
    }
    if !did {
        print("usage: swift brand/icon.swift --macos <iconset-dir> --android <res-dir> --ios <xcassets-dir> [--preview <png>]")
        exit(2)
    }
} catch {
    FileHandle.standardError.write("icon: \(error)\n".data(using: .utf8)!)
    exit(1)
}
