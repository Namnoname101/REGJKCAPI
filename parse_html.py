from bs4 import BeautifulSoup
import re

with open('rr3311html.txt', 'r', encoding='utf-8') as f:
    soup = BeautifulSoup(f.read(), 'html.parser')

for el in soup.find_all(string=re.compile('(?i)đăng ký|register')):
    parent = el.parent
    if not parent.find_parent('form'):
        print(f"Found non-form element: <{parent.name} class='{parent.get('class')}'> {el.strip()}")
