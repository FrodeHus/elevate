class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.0.1"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "fe994982ee05a51589debe7b301e8f20325c598acea5dab8a45434780702c607"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "a6eecb32288d3e254d2515f649d5dd55fe05625d8bcf44a104fc05de14c14bd6"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "2fa7543968dcbc1b9f17097a2b566f05eb57454d619e70a4cd1b1b7854589713"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "5c8521a9aeee2ef3ecaf72a9bd82f31c4ec4864ef3858b4f713dad9bd0eab1cc"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
