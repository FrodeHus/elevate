#!/usr/bin/env perl
# Regenerates the Entra built-in roles catalogue used by both apps from the
# markdown of https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference
# Usage (from the repo root):
#   perl shared/entra-roles/update-role-catalogue.pl permissions-reference.md > macos/Sources/ElevateCore/Resources/EntraBuiltInRoles.json
#   perl shared/entra-roles/update-role-catalogue.pl permissions-reference.md > windows/src/Elevate.Core/Resources/EntraBuiltInRoles.json
use strict; use warnings;
my @roles;
while (<>) {
    next unless /^\| (.+?) \| (.+?) \| ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) \|\s*$/;
    my ($name, $desc, $id) = ($1, $2, $3);
    my $priv = $desc =~ /privileged-label/ ? 'true' : 'false';
    $desc =~ s/\[!\[.*?\]\(.*?\)\]\(.*?\)//g;   # strip privileged badge markup
    $desc =~ s/\[(.*?)\]\(.*?\)/$1/g;            # unwrap links
    $desc =~ s/\s+$//; $desc =~ s/\\/\\\\/g; $desc =~ s/"/\\"/g;
    $name =~ s/"/\\"/g;
    push @roles, qq({"templateId":"$id","displayName":"$name","description":"$desc","isPrivileged":$priv});
}
print "[\n  " . join(",\n  ", @roles) . "\n]\n";
