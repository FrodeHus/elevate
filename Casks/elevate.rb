cask "elevate" do
  version "1.0.0"
  sha256 "8a367c356254e7c30605e28874d032d2892b295adbf5250d9cc51a016bbd3a85"

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
