class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.0.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "8d77e3c6e08d697caafa8824cd1743c3a43e1034a536625568fc8fabd87e4857"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "93b0643ba27e4909e314515b1bec1f9de6120b24ceb01221845fbb1edae40cff"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "0b02ee2e6fc8f98cbe9b68e2ba3971e130c278f3af1c84de26ccbe5f677d6177"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "4c296cfa13029822c2d24549d51687eb3debe145cf6a2611babb385fdf38111b"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
