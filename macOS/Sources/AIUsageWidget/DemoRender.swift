import AppKit
import SwiftUI
import UsageCore

/// `--render-demo <folder>`: draws the panel and the menu-bar item with built-in sample readings to PNG files, for
/// screenshots. It reads no sign-in, sends nothing and doesn't start the widget.
@MainActor
enum DemoRender {
    static func run(folder: URL) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // Default settings, whatever this Mac has saved; the argument domain wins and is never written to disk.
        UserDefaults.standard.setVolatileDomain(["topmost": false, "watchedInterval": 180.0, "showAntigravity": true],
                                                forName: UserDefaults.argumentDomain)
        let model = UsageModel(demo: true)
        try write(panel(model), to: folder.appendingPathComponent("macos-panel.png"))
        for dark in [false, true] {
            try write(menuBar(StatusIcon.image(model.menuReadings), dark: dark),
                      to: folder.appendingPathComponent(dark ? "macos-menubar-dark.png" : "macos-menubar-light.png"))
        }
    }

    /// Lays the panel out in an offscreen window, so it measures itself as on screen, then captures it.
    private static func panel(_ model: UsageModel) -> NSBitmapImageRep {
        let host = NSHostingView(rootView: WidgetView(model: model))
        let window = NSWindow(contentRect: NSRect(x: -10_000, y: -10_000, width: 300, height: 800), styleMask: .borderless,
                              backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.contentView = host
        window.orderFrontRegardless()
        // Two passes: the cards report their height after the first layout.
        for _ in 0..<2 {
            RunLoop.main.run(until: Date().addingTimeInterval(0.6))
            window.setContentSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
        }
        RunLoop.main.run(until: Date().addingTimeInterval(0.4))
        let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds)!
        host.cacheDisplay(in: host.bounds, to: bitmap)
        window.orderOut(nil)
        return bitmap
    }

    /// The template image on a strip coloured like a light or dark menu bar, at twice the point size.
    private static func menuBar(_ icon: NSImage, dark: Bool) -> NSBitmapImageRep {
        let padding = NSSize(width: 12, height: 5)
        let size = NSSize(width: ceil(icon.size.width) + padding.width * 2, height: icon.size.height + padding.height * 2)
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * 2), pixelsHigh: Int(size.height * 2),
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                      colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        bitmap.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        (dark ? NSColor(white: 0.13, alpha: 1) : NSColor(white: 0.93, alpha: 1)).setFill()
        NSBezierPath(roundedRect: NSRect(origin: .zero, size: size), xRadius: 6, yRadius: 6).fill()
        let tinted = NSImage(size: icon.size, flipped: false) { rect in
            icon.draw(in: rect)
            (dark ? NSColor.white : NSColor.black).withAlphaComponent(0.9).setFill()
            rect.fill(using: .sourceAtop)
            return true
        }
        tinted.draw(in: NSRect(origin: NSPoint(x: padding.width, y: padding.height), size: icon.size))
        NSGraphicsContext.restoreGraphicsState()
        return bitmap
    }

    private static func write(_ bitmap: NSBitmapImageRep, to url: URL) throws {
        guard let data = bitmap.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        try data.write(to: url, options: .atomic)
        print(url.path)
    }
}
