cask "elevate" do
  version "1.2.9"
  sha256 "8d224876ad83e020c7c211034f94951e924366d80a4ff2955c5e27b76989c1f9"

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
