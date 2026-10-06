"""Compile Bicep and verify private-network migration safety without deploying."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


def resources(template):
    items = template.get("resources", [])
    return list(items.values()) if isinstance(items, dict) else items


def deployment(template, name):
    return next(
        item
        for item in resources(template)
        if item["type"].lower() == "microsoft.resources/deployments"
        and item["name"] == name
    )


def walk_resources(template):
    for item in resources(template):
        yield item
        properties = item.get("properties", {})
        nested = properties.get("template") if isinstance(properties, dict) else None
        if nested:
            yield from walk_resources(nested)


class PrivateNetworkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        az = shutil.which("az")
        if not az:
            raise RuntimeError("Azure CLI with Bicep is required for infrastructure tests.")
        cls.temp = tempfile.TemporaryDirectory(prefix="profile-chat-network-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.templates = {}
        for name, relative in (
            ("app", Path("infra") / "main.bicep"),
            ("bootstrap", Path("network") / "infra" / "main.bicep"),
            ("cutover", Path("network") / "cutover" / "infra" / "main.bicep"),
            ("capacity", Path("capacity-update") / "infra" / "main.bicep"),
        ):
            output = Path(cls.temp.name) / f"{name}.json"
            result = subprocess.run(
                [az, "bicep", "build", "--file", str(ROOT / relative),
                 "--outfile", str(output)],
                capture_output=True,
                text=True,
                check=False,
            )
            if result.returncode:
                raise RuntimeError(f"{name} compilation failed:\n{result.stderr}")
            cls.templates[name] = json.loads(output.read_text(encoding="utf-8-sig"))
        cls.platform = deployment(
            cls.templates["app"], "workiq-profile-chat-platform"
        )["properties"]["template"]
        cls.network = deployment(
            cls.platform, "workiq-profile-chat-network"
        )["properties"]["template"]

    def test_capacity_update_only_changes_existing_model_capacity(self):
        template = self.templates["capacity"]
        self.assertEqual(template["parameters"]["capacity"]["defaultValue"], 200)
        entries = [r for r in walk_resources(template) if r["type"] != "Microsoft.Resources/deployments"]
        self.assertEqual(len(entries), 1)
        model = entries[0]
        self.assertEqual(model["type"], "Microsoft.CognitiveServices/accounts/deployments")
        self.assertEqual(model["sku"]["name"], "GlobalStandard")
        self.assertEqual(model["properties"]["model"]["version"], "2025-04-14")
        self.assertEqual(model["properties"]["model"]["name"], "gpt-4.1")
        config = (ROOT / "capacity-update" / "azure.yaml").read_text()
        self.assertNotIn("hooks:", config)
        self.assertNotIn("services:", config)

    def test_bootstrap_writes_only_network_and_deployments(self):
        for item in walk_resources(self.templates["bootstrap"]):
            resource_type = item["type"].lower()
            self.assertTrue(
                resource_type.startswith("microsoft.network/")
                or resource_type == "microsoft.resources/deployments",
                resource_type,
            )
        config = (ROOT / "network" / "azure.yaml").read_text()
        self.assertNotIn("hooks:", config)
        self.assertNotIn("services:", config)

    def test_cutover_does_not_write_models_or_auth_configuration(self):
        config = (ROOT / "network" / "cutover" / "azure.yaml").read_text()
        self.assertIn("path: infra", config)
        self.assertIn("module: main", config)
        self.assertNotIn("hooks:", config)
        self.assertNotIn("services:", config)
        allowed = {
            "microsoft.resources/deployments",
            "microsoft.web/sites/networkconfig",
            "microsoft.storage/storageaccounts",
            "microsoft.keyvault/vaults",
        }
        for item in walk_resources(self.templates["cutover"]):
            self.assertIn(item["type"].lower(), allowed)

    def test_bootstrap_and_app_share_network_definition(self):
        bootstrap_network = deployment(
            self.templates["bootstrap"], "workiq-profile-chat-network"
        )["properties"]["template"]
        self.assertEqual(self.network, bootstrap_network)

    def test_private_targets_and_dns(self):
        services = self.network["variables"]["privateServices"]
        self.assertEqual([s["name"] for s in services], ["blob", "vault"])
        self.assertIn("privatelink.blob.", services[0]["zone"])
        self.assertEqual(services[1]["zone"], "privatelink.vaultcore.azure.net")
        types = {r["type"].lower() for r in resources(self.network)}
        self.assertIn("microsoft.network/privateendpoints/privatednszonegroups", types)
        self.assertIn("microsoft.network/privatednszones/virtualnetworklinks", types)

    def test_cidrs_have_no_unapproved_defaults(self):
        for name in (
            "addressPrefix", "functionSubnetPrefix", "privateEndpointSubnetPrefix"
        ):
            self.assertNotIn("defaultValue", self.network["parameters"][name])
        network = next(
            r for r in resources(self.network)
            if r["type"].lower() == "microsoft.network/virtualnetworks"
        )
        subnets = network["properties"]["subnets"]
        self.assertEqual(len(subnets), 2)
        self.assertEqual(
            subnets[0]["properties"]["delegations"][0]["properties"]["serviceName"],
            "Microsoft.App/environments",
        )
        self.assertNotIn("delegations", subnets[1]["properties"])
        self.assertNotIn("serviceEndpoints", subnets[0]["properties"])

    def test_data_planes_private_with_auth_preserved(self):
        for name in ("function-storage", "key-vault"):
            params = deployment(self.platform, name)["properties"]["parameters"]
            self.assertEqual(params["publicNetworkAccess"]["value"], "Disabled")
            self.assertEqual(params["networkAcls"]["value"]["defaultAction"], "Deny")
            self.assertEqual(params["networkAcls"]["value"]["bypass"], "None")
        storage = deployment(self.platform, "function-storage")["properties"]["parameters"]
        self.assertFalse(storage["allowSharedKeyAccess"]["value"])
        self.assertFalse(storage["allowBlobPublicAccess"]["value"])
        vault = deployment(self.platform, "key-vault")["properties"]["parameters"]
        self.assertTrue(vault["enableRbacAuthorization"]["value"])
        self.assertTrue(vault["enablePurgeProtection"]["value"])
        for name in (
            "enableVaultForDeployment", "enableVaultForDiskEncryption",
            "enableVaultForTemplateDeployment",
        ):
            self.assertFalse(vault[name]["value"])
        self.assertNotIn("SecurityControl", json.dumps(self.templates["app"]))

    def test_function_waits_for_network_and_preserves_public_dependencies(self):
        function = deployment(self.platform, "function-app")
        self.assertIn(
            "privateNetwork",
            function["properties"]["parameters"]["virtualNetworkSubnetId"]["value"],
        )
        site = deployment(function["properties"]["template"], "function-site")
        self.assertIn("virtualNetworkSubnetId", site["properties"]["parameters"])
        auth = next(
            r for r in resources(function["properties"]["template"])
            if r["type"].lower() == "microsoft.web/sites/config"
        )
        self.assertTrue(auth["properties"]["globalValidation"]["requireAuthentication"])
        self.assertEqual(
            auth["properties"]["globalValidation"]["unauthenticatedClientAction"],
            "Return401",
        )
        for name in ("static-web-app", "foundry-account"):
            params = deployment(self.platform, name)["properties"]["parameters"]
            self.assertEqual(params["publicNetworkAccess"]["value"], "Enabled")

    def test_azd_parameter_bindings_are_complete(self):
        for name, path in (
            ("app", ROOT / "infra" / "main.parameters.json"),
            ("bootstrap", ROOT / "network" / "infra" / "main.parameters.json"),
            ("cutover", ROOT / "network" / "cutover" / "infra" / "main.parameters.json"),
        ):
            bindings = json.loads(path.read_text())["parameters"]
            self.assertEqual(set(bindings), set(self.templates[name]["parameters"]))


if __name__ == "__main__":
    unittest.main()
