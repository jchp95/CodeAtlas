import Foundation

@main
struct UTF8StreamDecoderTests {
    static func main() {
        var decoder = UTF8StreamDecoder()
        let source = "Build correcto — compilación: café ☕️ y carga 🚚\n"
        let bytes = Data(source.utf8)
        var decoded = ""
        // Split at every byte boundary to simulate arbitrary pipe reads.
        for byte in bytes {
            decoded += decoder.append(Data([byte]))
        }
        decoded += decoder.finish()
        precondition(decoded == source, "UTF-8 split across pipe chunks was corrupted")

        var finalChunkDecoder = UTF8StreamDecoder()
        let emoji = Data("🚚".utf8)
        let prefix = Data(emoji.prefix(2))
        let suffix = Data(emoji.dropFirst(2))
        precondition(finalChunkDecoder.append(prefix).isEmpty, "incomplete UTF-8 sequence was emitted early")
        precondition(finalChunkDecoder.append(suffix) == "🚚", "split four-byte UTF-8 sequence was not reconstructed")
        precondition(finalChunkDecoder.finish().isEmpty, "decoder retained bytes after a complete sequence")
        print("UTF-8 stream decoder tests passed: arbitrary byte splits, accents, emoji, and finalization.")
    }
}
