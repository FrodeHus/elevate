cask "elevate" do
  version "1.6.4"
  sha256 "4d94ef6c5e01d81795005f7a98f682433a081e9e67710f74f0837b39ffcd87af"

  url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/Elevate-#{version}.pkg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation, menu bar and terminal"
  homepage "https://github.com/FrodeHus/elevate"

  depends_on macos: :tahoe

  # The installer package puts Elevate.app in /Applications and links /usr/local/bin/elevate to
  # the CLI bundled in Elevate.app/Contents/Helpers (Apple Silicon; Intel Macs use the
  # elevate-cli archive). A pkg needs sudo, which is why Homebrew asks for a password.
  pkg "Elevate-#{version}.pkg"

  # /usr/local/bin/elevate is the pkg's link on Apple Silicon; on Intel it belongs to the
  # elevate-cli formula (Homebrew's prefix is /usr/local there), so it is not ours to delete.
  on_arm do
    uninstall quit:    "no.reothor.elevate",
              pkgutil: "no.reothor.elevate",
              delete:  "/usr/local/bin/elevate"
  end
  on_intel do
    uninstall quit:    "no.reothor.elevate",
              pkgutil: "no.reothor.elevate"
  end

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]
end
