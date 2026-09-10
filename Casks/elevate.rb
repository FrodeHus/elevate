cask "elevate" do
  version "1.6.0"
  sha256 "0855efc0f446d5eda1284823c4823268b8e848e6a8f7faf5d4f8bdb8d543e393"

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
