class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.6.7"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "c7d500aec654f8a22cc56f225a726fdd9224d1ffdd98e7f894c1fdbba8b03932"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "f5210e922427f0cb56ae4a13206934a8a3a9ea9a79cdbcc47d2154aa52e681c5"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "5dd98bb194d65f0e62d14e66cc2491911a1c70169693019d88f4b31f522ee226"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "5472858dca00c8debdfa5c54030031830e4cdfa09b6668eda03f37b7d4121b2e"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
