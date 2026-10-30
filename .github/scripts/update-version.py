import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


def update_version(path: Path, version: str) -> None:
    if not version.strip():
        raise ValueError("Version must not be empty")

    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    tree = ET.parse(path, parser=parser)
    versions = tree.findall("./PropertyGroup/Version")
    if tree.getroot().tag != "Project" or len(versions) != 1:
        raise ValueError("Expected exactly one shared PropertyGroup/Version element")

    versions[0].text = version
    ET.indent(tree, space="  ")
    path.write_text(ET.tostring(tree.getroot(), encoding="unicode") + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    arguments = argparse.ArgumentParser()
    arguments.add_argument("version")
    arguments.add_argument("--file", type=Path, default=Path(__file__).resolve().parents[2] / "Directory.Build.props")
    options = arguments.parse_args()
    update_version(options.file, options.version)