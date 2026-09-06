cask "elevate" do
  version "1.2.1"
  sha256 "71c9a9a5a73799f373de90dba768043b3ce535213b990a8ad120a5b58fdcfc24"

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
