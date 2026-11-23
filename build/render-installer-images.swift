// Renders the MSI wizard bitmaps for WiX (WixUI): the 493×58 banner (top strip of the inner pages) and the 493×312
// dialog image (left of the Welcome / Finish pages) with the OCCA for Windows logo on the app's paper colour.
// WiX wants 24-bit BMP files.
//
// Usage: swift build/render-installer-images.swift src/Occa.App/Assets/AppLogo.png installer/wix
import CoreGraphics
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers

let logoURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outDir = URL(fileURLWithPath: CommandLine.arguments[2])
let logo = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithURL(logoURL as CFURL, nil)!, 0, nil)!
let space = CGColorSpace(name: CGColorSpace.sRGB)!
let paper = CGColor(srgbRed: 0xFA / 255, green: 0xF7 / 255, blue: 0xF2 / 255, alpha: 1)
let ink = CGColor(srgbRed: 0x2B / 255, green: 0x2A / 255, blue: 0x28 / 255, alpha: 1)
let muted = CGColor(srgbRed: 0x6B / 255, green: 0x66 / 255, blue: 0x5E / 255, alpha: 1)
let orange = CGColor(srgbRed: 0xEE / 255, green: 0x7A / 255, blue: 0x1E / 255, alpha: 1)

func text(_ s: String, size: CGFloat, weight: String, color: CGColor, at p: CGPoint, in ctx: CGContext) {
    let font = CTFontCreateWithName(("Helvetica" + (weight.isEmpty ? "" : "-" + weight)) as CFString, size, nil)
    let attrs = [kCTFontAttributeName: font, kCTForegroundColorAttributeName: color] as CFDictionary
    let line = CTLineCreateWithAttributedString(CFAttributedStringCreate(nil, s as CFString, attrs))
    ctx.textPosition = p
    CTLineDraw(line, ctx)
}

func render(width: Int, height: Int, draw: (CGContext) -> Void) -> CGImage {
    // No alpha: BMP output is 24-bit.
    let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                        bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
    ctx.interpolationQuality = .high
    ctx.setFillColor(paper)
    ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
    draw(ctx)
    return ctx.makeImage()!
}

func write(_ image: CGImage, _ name: String) {
    let dest = CGImageDestinationCreateWithURL(outDir.appendingPathComponent(name) as CFURL, UTType.bmp.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    CGImageDestinationFinalize(dest)
}

// Dialog (493×312): WixUI prints the page text from x ≈ 135, so the artwork stays in the left strip (like the stock bitmap).
write(render(width: 493, height: 312) { ctx in
    ctx.draw(logo, in: CGRect(x: 8, y: 312 - 30 - 112, width: 112, height: 112))
    text("OCCA", size: 22, weight: "Bold", color: ink, at: CGPoint(x: 26, y: 146), in: ctx)
    text("for Windows", size: 12, weight: "", color: muted, at: CGPoint(x: 26, y: 128), in: ctx)
    ctx.setFillColor(orange)
    ctx.fill(CGRect(x: 26, y: 116, width: 72, height: 2))
    text("Orienteering", size: 10, weight: "", color: muted, at: CGPoint(x: 26, y: 98), in: ctx)
    text("Cakra CAD", size: 10, weight: "", color: muted, at: CGPoint(x: 26, y: 85), in: ctx)
    text("Application", size: 10, weight: "", color: muted, at: CGPoint(x: 26, y: 72), in: ctx)
}, "Dialog.bmp")

// Banner (493×58): small logo on the right, as WixUI leaves the left side for the page title.
write(render(width: 493, height: 58) { ctx in
    ctx.draw(logo, in: CGRect(x: 493 - 58, y: 1, width: 56, height: 56))
    ctx.setFillColor(orange)
    ctx.fill(CGRect(x: 0, y: 0, width: 493, height: 2))
}, "Banner.bmp")
print("installer images written")
