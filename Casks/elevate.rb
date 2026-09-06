cask "elevate" do
  version "1.2.0"
  sha256 "34e7711c055449362602404d0bdc96c307c9dd9a016f43294b54a45df0dad95d"

  url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/Elevate-#{version}.dmg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar"
  homepage "https://github.com/FrodeHus/elevate"

  depends_on macos: :tahoe

  app "Elevate.app"

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]
  caveats "This build is not signed with a Developer ID. After installing, run: xattr -d com.apple.quarantine /Applications/Elevate.app — or open the app once and approve it under System Settings > Privacy & Security > Open Anyway."
end
