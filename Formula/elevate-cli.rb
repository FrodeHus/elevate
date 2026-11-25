class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.12.0"
  license "MIT"

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "2026-09-10", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "77ae90cf4e29ef60c7c177103cbbc193c3532f0e117ea463366623507bb78d34"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "c40ab3c7113fc24cce688da4c16670245f142814b4db01cbab210b00141b9f32"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "7c7c043b3cf9d2b6a7f56ac792b6c50bef1d9fb163ea0dbfe533d6ed302125bf"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "2c6a512353ccadb2a1c7568a4aa08bf8383b5bfdcab1af3d5d6c9256886dc605"
    end
  end

  def install
    bin.install "elevate"
    generate_completions_from_executable(bin/"elevate", "completion")
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate --version")
  end
end
