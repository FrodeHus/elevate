cask "elevate" do
  version "0.0.0"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/Elevate-#{version}.dmg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar"
  homepage "https://github.com/FrodeHus/elevate"

  depends_on macos: ">= :tahoe"

  app "Elevate.app"

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]
  caveats "This build is not signed with a Developer ID and is not notarized. Install it with --no-quarantine, or open the app once and, when macOS blocks it, open System Settings > Privacy & Security, scroll to the message about Elevate and click Open Anyway, then confirm."
end
