import zipfile
import xml.etree.ElementTree as ET
import os
import re

pptx_path = r"f:\BKU\Intern\Host\papers\Slide dao tao Phan mem QLTS GD 3 - Tờ trình nghiệp vụ.pptx"
output_path = r"f:\BKU\Intern\Host\scratch\slide_dump.txt"

def dump_slides():
    if not os.path.exists(pptx_path):
        print(f"File not found: {pptx_path}")
        return

    with open(output_path, "w", encoding="utf-8") as out:
        with zipfile.ZipFile(pptx_path, 'r') as z:
            slide_entries = [name for name in z.namelist() if name.startswith("ppt/slides/slide") and name.endswith(".xml")]
            def slide_number(name):
                match = re.search(r'slide(\d+)\.xml$', name)
                return int(match.group(1)) if match else 999
            
            slide_entries.sort(key=slide_number)
            
            for entry in slide_entries:
                out.write(f"\n=================== {entry} ===================\n")
                xml_content = z.read(entry)
                root = ET.fromstring(xml_content)
                
                # Check shapes
                for paragraph in root.iter('{http://schemas.openxmlformats.org/drawingml/2006/main}p'):
                    para_parts = []
                    for child in paragraph:
                        if child.tag == '{http://schemas.openxmlformats.org/drawingml/2006/main}r':
                            for t in child.iter('{http://schemas.openxmlformats.org/drawingml/2006/main}t'):
                                if t.text:
                                    para_parts.append(t.text)
                        elif child.tag == '{http://schemas.openxmlformats.org/drawingml/2006/main}tab':
                            para_parts.append("[TAB]")
                        elif child.tag == '{http://schemas.openxmlformats.org/drawingml/2006/main}br':
                            para_parts.append("\n")
                    
                    para_text = "".join(para_parts)
                    if para_text.strip():
                        out.write(f"{repr(para_text)}\n")

if __name__ == "__main__":
    dump_slides()
