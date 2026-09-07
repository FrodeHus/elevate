cask "elevate" do
  version "1.2.7"
  sha256 "4e4eeec91af73b7aa681caf9131efb5882c04e682a7b06c2bc25425c55e62b41"

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
end
