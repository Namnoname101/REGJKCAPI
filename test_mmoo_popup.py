import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(10)

soup = BeautifulSoup(driver.page_source, 'html.parser')
buttons = soup.find_all(['button', 'a', 'span', 'div'])

for b in buttons:
    text = b.text.strip()
    if 'Đóng' in text and len(text) < 10:
        print('Đóng btn:', b.name, b.attrs, text)
    if 'TIN TỨC' in text and len(text) < 30:
        print('Title:', b.name, b.attrs, text)
        
closes = soup.find_all(class_=lambda c: c and 'close' in c.lower())
for c in closes:
    print('Close class:', c.name, c.attrs)

driver.quit()
