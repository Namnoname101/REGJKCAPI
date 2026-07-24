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

try:
    reg_btn = driver.find_element(webdriver.common.by.By.CSS_SELECTOR, "button[ng-click*='openRegisterModal']")
    driver.execute_script('arguments[0].click();', reg_btn)
    time.sleep(5)
except Exception as e:
    pass

soup = BeautifulSoup(driver.page_source, 'html.parser')
inputs = soup.find_all('input')

with open('mmoo_dom.txt', 'w', encoding='utf-8') as f:
    for i in inputs:
        f.write('input: ' + str(i.attrs) + '\n')
    
    buttons = soup.find_all('button')
    for b in buttons:
        if 'register' in str(b.attrs).lower() or 'submit' in str(b.attrs).lower():
            f.write('btn: ' + str(b.attrs) + ' ' + b.text.strip()[:20] + '\n')

driver.quit()
